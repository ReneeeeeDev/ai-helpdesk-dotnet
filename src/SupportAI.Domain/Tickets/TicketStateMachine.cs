namespace SupportAI.Domain.Tickets;

/// <summary>
/// Transiciones de estado permitidas para un ticket.
/// <code>
/// New ──► Triaged ──► Open ⇄ Pending ──► Resolved ──► Closed
///  │         │         │                  │  ▲
///  └─────────┴─────────┴──► Closed        └──┘ (reabrir → Open)
/// </code>
/// </summary>
public static class TicketStateMachine
{
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Transitions = new()
    {
        [TicketStatus.New] = [TicketStatus.Triaged, TicketStatus.Open, TicketStatus.Closed],
        [TicketStatus.Triaged] = [TicketStatus.Open, TicketStatus.Pending, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.Open] = [TicketStatus.Pending, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.Pending] = [TicketStatus.Open, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.Resolved] = [TicketStatus.Open, TicketStatus.Closed],
        [TicketStatus.Closed] = [],
    };

    public static bool CanTransition(TicketStatus from, TicketStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static IReadOnlyList<TicketStatus> AllowedFrom(TicketStatus from) =>
        Transitions.TryGetValue(from, out var allowed) ? allowed : [];

    public static bool IsTerminal(TicketStatus status) => status == TicketStatus.Closed;

    /// <summary>Estados en los que el reloj de SLA de resolución está detenido.</summary>
    public static bool PausesSla(TicketStatus status) => status is TicketStatus.Pending or TicketStatus.Resolved or TicketStatus.Closed;
}
