using SupportAI.Contracts.Ai;
using SupportAI.Contracts.Common;

namespace SupportAI.Contracts.Tickets;

public sealed record CreateTicketRequest(string Subject, string Description, TicketChannel Channel = TicketChannel.Web);

public sealed record AddMessageRequest(string Body, bool IsInternal = false);

public sealed record ChangeStatusRequest(TicketStatus Status);

public sealed record ChangePriorityRequest(TicketPriority Priority);

public sealed record AssignTicketRequest(Guid? AssigneeId, Guid? TeamId);

/// <summary>Mensaje SignalR "TicketChanged": el cliente vuelve a pedir el ticket a la API.</summary>
public sealed record TicketChangedNotification(Guid TicketId, string Kind);

/// <summary>Filtros del inbox. Todos opcionales.</summary>
public sealed record TicketListQuery
{
    public TicketStatus? Status { get; init; }

    public TicketPriority? Priority { get; init; }

    public Guid? AssigneeId { get; init; }

    public Guid? TeamId { get; init; }

    public string? Category { get; init; }

    public string? Search { get; init; }

    public bool? NeedsReview { get; init; }

    public bool? SlaBreached { get; init; }

    public bool OnlyMine { get; init; }

    public bool IncludeClosed { get; init; }

    public TicketSort Sort { get; init; } = TicketSort.Newest;

    public string? Cursor { get; init; }

    public int PageSize { get; init; } = 25;
}

public sealed record TicketSummaryDto(
    Guid Id,
    long Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketChannel Channel,
    string? Category,
    Sentiment? Sentiment,
    Guid RequesterId,
    string? RequesterName,
    Guid? AssigneeId,
    string? AssigneeName,
    Guid? TeamId,
    string? TeamName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    bool SlaBreached,
    bool NeedsHumanReview,
    int MessageCount);

public sealed record TicketMessageDto(
    Guid Id,
    Guid? AuthorId,
    string? AuthorName,
    MessageAuthorType AuthorType,
    string Body,
    bool IsInternal,
    DateTimeOffset CreatedAt);

public sealed record TicketActivityDto(string Type, string Description, Guid? ActorId, string? ActorName, DateTimeOffset OccurredAt);

public sealed record TicketDetailDto(
    TicketSummaryDto Summary,
    string Description,
    string? Language,
    string? AiSummary,
    IReadOnlyList<string> Tags,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? SlaPausedAt,
    IReadOnlyList<TicketMessageDto> Messages,
    IReadOnlyList<TicketActivityDto> Activities,
    IReadOnlyList<TicketStatus> AllowedTransitions,
    TriageResultDto? LatestTriage,
    string RowVersion);
