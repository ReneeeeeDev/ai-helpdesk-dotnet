using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SupportAI.Domain.Common;
using SupportAI.Infrastructure.Persistence.Outbox;

namespace SupportAI.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Convierte los eventos de dominio pendientes en filas de <see cref="OutboxMessage"/> dentro del mismo SaveChanges,
/// es decir, en la misma transacción que el cambio de estado que los originó.
/// </summary>
internal sealed class DomainEventsToOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enqueue(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enqueue(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Enqueue(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker.Entries<AggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        if (aggregates.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var correlationId = Activity.Current?.TraceId.ToString();
        var outbox = context.Set<OutboxMessage>();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                outbox.Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    Type = DomainEventTypeRegistry.NameOf(domainEvent),
                    Payload = DomainEventTypeRegistry.Serialize(domainEvent),
                    TenantId = domainEvent.TenantId,
                    OccurredAt = domainEvent.OccurredAt,
                    Status = OutboxStatus.Pending,
                    NextAttemptAt = now,
                    CorrelationId = correlationId,
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
