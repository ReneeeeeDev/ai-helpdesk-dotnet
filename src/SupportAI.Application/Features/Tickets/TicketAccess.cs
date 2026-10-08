using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Tickets;

/// <summary>Reglas de visibilidad de tickets: el staff ve todo el tenant; un cliente, solo los suyos.</summary>
internal static class TicketAccess
{
    /// <summary>Filtra la consulta según el usuario. Los tickets ajenos de un cliente "no existen" (404, no 403).</summary>
    public static IQueryable<Ticket> VisibleTo(this IQueryable<Ticket> tickets, ICurrentUser user) =>
        user.IsStaff ? tickets : tickets.Where(t => t.RequesterId == user.RequiredUserId);

    public static async Task<Ticket> LoadForUpdateAsync(this IApplicationDbContext db, ICurrentUser user, Guid ticketId, CancellationToken ct) =>
        await db.Tickets.VisibleTo(user).FirstOrDefaultAsync(t => t.Id == ticketId, ct)
        ?? throw new NotFoundException("Ticket", ticketId);

    /// <summary>
    /// Concurrencia optimista desde la UI: si el cliente envía la versión que vio y ya cambió, devolvemos 409.
    /// EF Core repite la comprobación en el UPDATE con la columna rowversion.
    /// </summary>
    public static void EnsureVersion(this Ticket ticket, string? expectedRowVersion)
    {
        if (string.IsNullOrEmpty(expectedRowVersion))
        {
            return;
        }

        if (Convert.ToBase64String(ticket.RowVersion) != expectedRowVersion)
        {
            throw new ConflictException("El ticket fue modificado por otra persona. Recarga y vuelve a intentarlo.", "ticket_version_mismatch");
        }
    }
}
