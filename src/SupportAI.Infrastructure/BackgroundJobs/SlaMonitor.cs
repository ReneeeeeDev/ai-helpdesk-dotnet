using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Domain.Tickets;
using SupportAI.Infrastructure.Persistence;

namespace SupportAI.Infrastructure.BackgroundJobs;

/// <summary>
/// Detecta incumplimientos de SLA. Busca candidatos en todos los tenants (con IgnoreQueryFilters) y los evalúa
/// tenant a tenant en scopes aislados; los eventos TicketSlaBreached salen por el outbox.
/// </summary>
public sealed partial class SlaMonitor(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<WorkerOptions> options,
    ILogger<SlaMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.SlaCheckInterval, clock);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogError(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<(Guid TenantId, Guid TicketId)> candidates;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
            var rows = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
                .Where(t => !t.IsDeleted && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
                .Where(t =>
                    (!t.FirstResponseBreached && t.FirstRespondedAt == null && t.FirstResponseDueAt < now) ||
                    (!t.ResolutionBreached && t.SlaPausedAt == null && t.ResolutionDueAt < now))
                .OrderBy(t => t.ResolutionDueAt)
                .Select(t => new { t.TenantId, t.Id })
                .Take(500)
                .ToListAsync(ct);
            candidates = rows.Select(r => (r.TenantId, r.Id)).ToList();
        }

        var breached = 0;
        foreach (var group in candidates.GroupBy(c => c.TenantId))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantScope>().Use(group.Key);
            var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();

            var ids = group.Select(g => g.TicketId).ToList();
            var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
            breached += tickets.Count(t => t.EvaluateSla(now));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Otro usuario modificó el ticket a la vez: se reevalúa en la próxima pasada.
            }
        }

        if (breached > 0)
        {
            LogBreaches(logger, breached);
        }

        return breached;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error en el monitor de SLA")]
    private static partial void LogError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor de SLA: {Count} incumplimientos nuevos")]
    private static partial void LogBreaches(ILogger logger, int count);
}
