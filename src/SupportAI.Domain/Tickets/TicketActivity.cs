using SupportAI.Domain.Common;

namespace SupportAI.Domain.Tickets;

public enum TicketActivityType
{
    Created = 0,
    StatusChanged = 1,
    PriorityChanged = 2,
    Assigned = 3,
    Triaged = 4,
    TriageReviewed = 5,
    SlaBreached = 6,
    FlaggedForReview = 7,
}

/// <summary>Entrada del historial de auditoría de un ticket (append-only).</summary>
public sealed class TicketActivity : Entity
{
    private TicketActivity()
    {
    }

    internal TicketActivity(Guid ticketId, TicketActivityType type, string description, Guid? actorId, DateTimeOffset occurredAt)
        : base(NewId())
    {
        TicketId = ticketId;
        Type = type;
        Description = description.Length > 500 ? description[..500] : description;
        ActorId = actorId;
        OccurredAt = occurredAt;
    }

    public Guid TicketId { get; private set; }

    public TicketActivityType Type { get; private set; }

    public string Description { get; private set; } = string.Empty;

    /// <summary>Null cuando el actor es el sistema o la IA.</summary>
    public Guid? ActorId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}
