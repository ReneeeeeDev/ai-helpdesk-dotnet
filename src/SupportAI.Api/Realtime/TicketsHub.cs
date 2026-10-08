using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SupportAI.Application.Abstractions.Notifications;
using SupportAI.Contracts.Tickets;
using SupportAI.Domain.Users;
using SupportAI.Infrastructure.Identity;

namespace SupportAI.Api.Realtime;

/// <summary>
/// Hub de notificaciones. Cada conexión entra al grupo de su usuario y, si es staff, al grupo de staff de su tenant.
/// El cliente solo recibe "algo cambió en el ticket X" y vuelve a pedir los datos a la API (que aplica la autorización).
/// </summary>
[Authorize]
public sealed class TicketsHub : Hub
{
    public const string Path = "/hubs/tickets";
    public const string TicketChangedMethod = "TicketChanged";

    public static string StaffGroup(Guid tenantId) => $"tenant:{tenantId}:staff";

    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (Guid.TryParse(user?.FindFirst(SupportAIClaims.UserId)?.Value, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        var isStaff = Enum.TryParse<UserRole>(user?.FindFirst(SupportAIClaims.Role)?.Value, out var role) && role != UserRole.Customer;
        if (isStaff && Guid.TryParse(user?.FindFirst(SupportAIClaims.TenantId)?.Value, out var tenantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup(tenantId));
        }

        await base.OnConnectedAsync();
    }
}

internal sealed class SignalRTicketNotifier(IHubContext<TicketsHub> hub) : ITicketNotifier
{
    public async Task TicketChangedAsync(Guid tenantId, Guid ticketId, Guid requesterId, string kind, bool staffOnly, CancellationToken ct)
    {
        var payload = new TicketChangedNotification(ticketId, kind);
        await hub.Clients.Group(TicketsHub.StaffGroup(tenantId)).SendAsync(TicketsHub.TicketChangedMethod, payload, ct);
        if (!staffOnly)
        {
            await hub.Clients.Group(TicketsHub.UserGroup(requesterId)).SendAsync(TicketsHub.TicketChangedMethod, payload, ct);
        }
    }
}
