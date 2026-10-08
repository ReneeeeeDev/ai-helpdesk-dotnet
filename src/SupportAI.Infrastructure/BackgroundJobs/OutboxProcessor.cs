using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Events;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Domain.Common;
using SupportAI.Infrastructure.Persistence;
using SupportAI.Infrastructure.Persistence.Outbox;

namespace SupportAI.Infrastructure.BackgroundJobs;

public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    /// <summary>Permite desactivar los workers (p.ej. en tests de integración, que los invocan manualmente).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int OutboxBatchSize { get; set; } = 20;

    public int OutboxMaxAttempts { get; set; } = 8;

    /// <summary>Tiempo que un mensaje queda "reservado" por una instancia antes de poder reintentarse en otra.</summary>
    public TimeSpan OutboxLease { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SlaCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan ProcessedRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Consume <see cref="OutboxMessage"/> y despacha cada evento a sus <see cref="IDomainEventHandler{TEvent}"/>.
/// Seguro con varias instancias: el claim es un único UPDATE con UPDLOCK/READPAST que "alquila" el lote.
/// </summary>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<WorkerOptions> options,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IDomainEvent, CancellationToken, Task>> Dispatchers = new();
    private static readonly MethodInfo DispatchMethod = typeof(OutboxProcessor).GetMethod(nameof(DispatchAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var cleanupEvery = TimeSpan.FromHours(1);
        var lastCleanup = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(options.Value.OutboxPollInterval, clock);

        do
        {
            try
            {
                // Vacía la cola mientras haya trabajo; luego espera al siguiente tick.
                while (await ProcessBatchAsync(stoppingToken) == options.Value.OutboxBatchSize && !stoppingToken.IsCancellationRequested)
                {
                }

                if (clock.GetUtcNow() - lastCleanup > cleanupEvery)
                {
                    await CleanupAsync(stoppingToken);
                    lastCleanup = clock.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Procesa un lote. Público para poder invocarlo de forma determinista desde los tests.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var leaseUntil = now + settings.OutboxLease;

        List<OutboxMessage> batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
            batch = await db.OutboxMessages
                .FromSqlInterpolated($"""
                    WITH batch AS (
                        SELECT TOP ({settings.OutboxBatchSize}) *
                        FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                        WHERE Status = 'Pending' AND NextAttemptAt <= {now}
                        ORDER BY OccurredAt
                    )
                    UPDATE batch SET NextAttemptAt = {leaseUntil}
                    OUTPUT inserted.*
                    """)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        foreach (var message in batch.OrderBy(m => m.OccurredAt))
        {
            await ProcessMessageAsync(message, ct);
        }

        return batch.Count;
    }

    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantScope>().Use(message.TenantId);
                var domainEvent = DomainEventTypeRegistry.Deserialize(message.Type, message.Payload);
                var dispatcher = Dispatchers.GetOrAdd(domainEvent.GetType(), static type =>
                    DispatchMethod.MakeGenericMethod(type).CreateDelegate<Func<IServiceProvider, IDomainEvent, CancellationToken, Task>>());

                using (logger.BeginScope(new Dictionary<string, object?> { ["OutboxMessageId"] = message.Id, ["CorrelationId"] = message.CorrelationId }))
                {
                    await dispatcher(scope.ServiceProvider, domainEvent, ct);
                }
            }

            await UpdateAsync(message.Id, OutboxStatus.Processed, message.Attempts + 1, clock.GetUtcNow(), null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var attempts = message.Attempts + 1;
            var deadLetter = attempts >= settings.OutboxMaxAttempts;
            // Backoff exponencial: 5 s, 10 s, 20 s... hasta 10 min.
            var delay = TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, attempts - 1)));
            var error = $"{ex.GetType().Name}: {ex.Message}";
            await UpdateAsync(message.Id, deadLetter ? OutboxStatus.DeadLetter : OutboxStatus.Pending, attempts, clock.GetUtcNow() + delay, error, ct);

            if (deadLetter)
            {
                LogDeadLetter(logger, ex, message.Id, message.Type, attempts);
            }
            else
            {
                LogRetry(logger, message.Id, message.Type, attempts, delay.TotalSeconds, ex.Message);
            }
        }
    }

    private async Task UpdateAsync(Guid id, OutboxStatus status, int attempts, DateTimeOffset when, string? error, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
        var truncated = error is { Length: > 2000 } ? error[..2000] : error;
        DateTimeOffset? processedAt = status == OutboxStatus.Processed ? when : null;

        await db.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, status)
            .SetProperty(m => m.Attempts, attempts)
            .SetProperty(m => m.NextAttemptAt, when)
            .SetProperty(m => m.ProcessedAt, processedAt)
            .SetProperty(m => m.LastError, truncated), ct);
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
        var now = clock.GetUtcNow();
        var threshold = now - options.Value.ProcessedRetention;

        var outbox = await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Processed && m.ProcessedAt < threshold).ExecuteDeleteAsync(ct);
        var idempotency = await db.IdempotencyRecords.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < threshold).ExecuteDeleteAsync(ct);
        LogCleanup(logger, outbox, idempotency, tokens);
    }

    private static async Task DispatchAsync<TEvent>(IServiceProvider services, IDomainEvent domainEvent, CancellationToken ct)
        where TEvent : IDomainEvent
    {
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
        {
            await handler.Handle((TEvent)domainEvent, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error en el bucle del outbox")]
    private static partial void LogLoopError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox {MessageId} ({Type}) falló (intento {Attempt}); reintento en {DelaySeconds} s: {Error}")]
    private static partial void LogRetry(ILogger logger, Guid messageId, string type, int attempt, double delaySeconds, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox {MessageId} ({Type}) movido a dead-letter tras {Attempt} intentos")]
    private static partial void LogDeadLetter(ILogger logger, Exception ex, Guid messageId, string type, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Limpieza: {Outbox} mensajes de outbox, {Idempotency} claves de idempotencia, {Tokens} refresh tokens")]
    private static partial void LogCleanup(ILogger logger, int outbox, int idempotency, int tokens);
}
