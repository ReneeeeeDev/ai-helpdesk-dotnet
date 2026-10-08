using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Events;
using SupportAI.Application.Abstractions.Notifications;
using SupportAI.Domain.Tickets.Events;

namespace SupportAI.Application.Features.Notifications;

/// <summary>Reenvía los eventos de tickets a la UI en tiempo real. Idempotente: notificar dos veces solo provoca un refresco extra.</summary>
internal sealed class TicketNotificationHandlers(IApplicationDbContext db, ITicketNotifier notifier) :
    IDomainEventHandler<TicketCreated>,
    IDomainEventHandler<TicketStatusChanged>,
    IDomainEventHandler<TicketAssigned>,
    IDomainEventHandler<TicketMessageAdded>,
    IDomainEventHandler<TicketTriaged>,
    IDomainEventHandler<TicketPriorityChanged>,
    IDomainEventHandler<TicketSlaBreached>
{
    public Task Handle(TicketCreated e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "created", staffOnly: false, ct);

    public Task Handle(TicketStatusChanged e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "status", staffOnly: false, ct);

    public Task Handle(TicketAssigned e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "assigned", staffOnly: true, ct);

    public Task Handle(TicketMessageAdded e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "message", staffOnly: e.IsInternal, ct);

    public Task Handle(TicketTriaged e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "triaged", staffOnly: true, ct);

    public Task Handle(TicketPriorityChanged e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "priority", staffOnly: true, ct);

    public Task Handle(TicketSlaBreached e, CancellationToken ct) => NotifyAsync(e.TenantId, e.TicketId, "sla_breached", staffOnly: true, ct);

    private async Task NotifyAsync(Guid tenantId, Guid ticketId, string kind, bool staffOnly, CancellationToken ct)
    {
        var requesterId = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => (Guid?)t.RequesterId).FirstOrDefaultAsync(ct);
        if (requesterId is null)
        {
            return; // El ticket fue borrado: nada que notificar.
        }

        await notifier.TicketChangedAsync(tenantId, ticketId, requesterId.Value, kind, staffOnly, ct);
    }
}
