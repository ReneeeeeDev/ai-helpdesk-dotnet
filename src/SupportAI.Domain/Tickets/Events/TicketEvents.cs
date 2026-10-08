using SupportAI.Domain.Common;

namespace SupportAI.Domain.Tickets.Events;

public sealed record TicketCreated(Guid TenantId, Guid TicketId, Guid RequesterId, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketStatusChanged(Guid TenantId, Guid TicketId, TicketStatus From, TicketStatus To, Guid? ActorId, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketPriorityChanged(Guid TenantId, Guid TicketId, TicketPriority From, TicketPriority To, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketAssigned(Guid TenantId, Guid TicketId, Guid? AssigneeId, Guid? TeamId, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketMessageAdded(Guid TenantId, Guid TicketId, Guid MessageId, MessageAuthorType AuthorType, bool IsInternal, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketTriaged(Guid TenantId, Guid TicketId, string? Category, TicketPriority Priority, bool ByAi, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);

public sealed record TicketSlaBreached(Guid TenantId, Guid TicketId, string Kind, DateTimeOffset OccurredAt)
    : DomainEvent(TenantId, OccurredAt);
