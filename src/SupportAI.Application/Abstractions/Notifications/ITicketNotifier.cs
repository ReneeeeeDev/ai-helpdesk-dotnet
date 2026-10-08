namespace SupportAI.Application.Abstractions.Notifications;

/// <summary>Notificación en tiempo real de cambios en tickets (implementada con SignalR en la API).</summary>
public interface ITicketNotifier
{
    /// <param name="kind">created | status | assigned | message | triaged | priority | sla_breached | reply_suggested.</param>
    Task TicketChangedAsync(Guid tenantId, Guid ticketId, Guid requesterId, string kind, bool staffOnly, CancellationToken ct);
}
