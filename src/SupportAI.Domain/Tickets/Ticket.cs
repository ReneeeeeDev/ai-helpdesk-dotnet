using SupportAI.Domain.Common;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Tickets.Events;

namespace SupportAI.Domain.Tickets;

/// <summary>Resultado de un triage (IA o humano) que se aplica al ticket.</summary>
public sealed record TriageDecision(
    string? Category,
    TicketPriority Priority,
    Sentiment? Sentiment,
    string? Language,
    Guid? TeamId,
    IReadOnlyList<string> Tags,
    string? Summary);

/// <summary>
/// Agregado principal. Protege la máquina de estados, el SLA (con pausa en Pending/Resolved) y el historial.
/// </summary>
public sealed class Ticket : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxSubjectLength = 200;
    public const int MaxDescriptionLength = 20_000;

    private readonly List<TicketMessage> _messages = [];
    private readonly List<TicketActivity> _activities = [];
    private List<string> _tags = [];

    private Ticket()
    {
    }

    private Ticket(Guid id, Guid tenantId, Guid requesterId, string subject, string description, TicketChannel channel, TicketPriority priority, DateTimeOffset now)
        : base(id)
    {
        TenantId = Guard.NotEmpty(tenantId, nameof(tenantId));
        RequesterId = Guard.NotEmpty(requesterId, nameof(requesterId));
        Subject = Guard.NotNullOrWhiteSpace(subject, nameof(subject), MaxSubjectLength);
        Description = Guard.NotNullOrWhiteSpace(description, nameof(description), MaxDescriptionLength);
        Channel = channel;
        Priority = priority;
        Status = TicketStatus.New;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Número correlativo legible (#1042). Lo asigna una SEQUENCE de SQL Server.</summary>
    public long Number { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public TicketStatus Status { get; private set; }

    public TicketPriority Priority { get; private set; }

    public TicketChannel Channel { get; private set; }

    public string? Category { get; private set; }

    public Sentiment? Sentiment { get; private set; }

    public string? Language { get; private set; }

    public string? AiSummary { get; private set; }

    public bool NeedsHumanReview { get; private set; }

    public Guid RequesterId { get; private set; }

    public Guid? AssigneeId { get; private set; }

    public Guid? TeamId { get; private set; }

    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    // --- SLA ---
    public DateTimeOffset? FirstResponseDueAt { get; private set; }

    public DateTimeOffset? ResolutionDueAt { get; private set; }

    public DateTimeOffset? FirstRespondedAt { get; private set; }

    public DateTimeOffset? SlaPausedAt { get; private set; }

    public TimeSpan SlaPausedDuration { get; private set; }

    public bool FirstResponseBreached { get; private set; }

    public bool ResolutionBreached { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    // --- Auditoría / concurrencia ---
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Token de concurrencia optimista (rowversion de SQL Server).</summary>
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<TicketMessage> Messages => _messages.AsReadOnly();

    public IReadOnlyList<TicketActivity> Activities => _activities.AsReadOnly();

    public bool IsSlaBreached => FirstResponseBreached || ResolutionBreached;

    public static Ticket Create(
        Guid tenantId,
        Guid requesterId,
        string subject,
        string description,
        TicketChannel channel,
        SlaPolicy? slaPolicy,
        DateTimeOffset now,
        TicketPriority priority = TicketPriority.Normal)
    {
        var ticket = new Ticket(NewId(), tenantId, requesterId, subject, description, channel, priority, now);
        ticket.RecalculateSla(slaPolicy);
        ticket.Log(TicketActivityType.Created, $"Ticket creado por canal {channel}.", requesterId, now);
        ticket.Raise(new TicketCreated(tenantId, ticket.Id, requesterId, now));
        return ticket;
    }

    public void ChangeStatus(TicketStatus to, Guid? actorId, DateTimeOffset now)
    {
        if (Status == to)
        {
            return;
        }

        if (!TicketStateMachine.CanTransition(Status, to))
        {
            throw new DomainException($"No se puede pasar un ticket de {Status} a {to}.", "ticket_invalid_transition");
        }

        var from = Status;
        UpdateSlaClock(from, to, now);
        Status = to;

        switch (to)
        {
            case TicketStatus.Resolved:
                ResolvedAt = now;
                break;
            case TicketStatus.Closed:
                ClosedAt = now;
                ResolvedAt ??= now;
                break;
            case TicketStatus.Open when from == TicketStatus.Resolved:
                ResolvedAt = null;
                break;
        }

        Log(TicketActivityType.StatusChanged, $"Estado: {from} → {to}.", actorId, now);
        Raise(new TicketStatusChanged(TenantId, Id, from, to, actorId, now));
    }

    public void ChangePriority(TicketPriority priority, SlaPolicy? slaPolicy, Guid? actorId, DateTimeOffset now)
    {
        EnsureNotClosed();
        if (Priority == priority)
        {
            return;
        }

        var from = Priority;
        Priority = priority;
        RecalculateSla(slaPolicy);
        Log(TicketActivityType.PriorityChanged, $"Prioridad: {from} → {priority}.", actorId, now);
        Raise(new TicketPriorityChanged(TenantId, Id, from, priority, now));
    }

    public void Assign(Guid? assigneeId, Guid? teamId, Guid? actorId, DateTimeOffset now)
    {
        EnsureNotClosed();
        if (AssigneeId == assigneeId && TeamId == teamId)
        {
            return;
        }

        AssigneeId = assigneeId;
        TeamId = teamId;
        Log(TicketActivityType.Assigned, $"Asignado a agente {assigneeId?.ToString() ?? "-"} / equipo {teamId?.ToString() ?? "-"}.", actorId, now);
        Raise(new TicketAssigned(TenantId, Id, assigneeId, teamId, now));
    }

    public TicketMessage AddMessage(Guid? authorId, MessageAuthorType authorType, string body, bool isInternal, DateTimeOffset now)
    {
        EnsureNotClosed();
        if (isInternal && authorType == MessageAuthorType.Customer)
        {
            throw new DomainException("Un cliente no puede crear notas internas.", "ticket_internal_note_forbidden");
        }

        var message = new TicketMessage(Id, authorId, authorType, body, isInternal, now);
        _messages.Add(message);

        if (!isInternal)
        {
            if (authorType == MessageAuthorType.Agent)
            {
                FirstRespondedAt ??= now;
                if (Status is TicketStatus.New or TicketStatus.Triaged)
                {
                    ChangeStatus(TicketStatus.Open, authorId, now);
                }
            }
            else if (authorType == MessageAuthorType.Customer && Status is TicketStatus.Pending or TicketStatus.Resolved)
            {
                // El cliente respondió: el ticket vuelve a la cola de trabajo.
                ChangeStatus(TicketStatus.Open, authorId, now);
            }
        }

        Raise(new TicketMessageAdded(TenantId, Id, message.Id, authorType, isInternal, now));
        return message;
    }

    /// <summary>Aplica un triage (de IA o manual). Si la prioridad cambia, recalcula el SLA.</summary>
    public void ApplyTriage(TriageDecision decision, SlaPolicy? slaPolicy, Guid? actorId, bool byAi, DateTimeOffset now)
    {
        EnsureNotClosed();

        Category = string.IsNullOrWhiteSpace(decision.Category) ? Category : decision.Category.Trim().ToLowerInvariant();
        Sentiment = decision.Sentiment ?? Sentiment;
        Language = string.IsNullOrWhiteSpace(decision.Language) ? Language : decision.Language.Trim().ToLowerInvariant();
        AiSummary = string.IsNullOrWhiteSpace(decision.Summary) ? AiSummary : decision.Summary.Trim();
        _tags = Guard.NormalizeTags(_tags.Concat(decision.Tags));
        NeedsHumanReview = false;

        if (decision.TeamId is not null && TeamId != decision.TeamId)
        {
            Assign(AssigneeId, decision.TeamId, actorId, now);
        }

        if (Priority != decision.Priority)
        {
            ChangePriority(decision.Priority, slaPolicy, actorId, now);
        }

        if (Status == TicketStatus.New)
        {
            ChangeStatus(TicketStatus.Triaged, actorId, now);
        }

        Log(TicketActivityType.Triaged, $"Triage {(byAi ? "IA" : "manual")}: categoría={Category ?? "-"}, prioridad={Priority}.", actorId, now);
        Raise(new TicketTriaged(TenantId, Id, Category, Priority, byAi, now));
    }

    public void FlagForHumanReview(string reason, DateTimeOffset now)
    {
        NeedsHumanReview = true;
        Log(TicketActivityType.FlaggedForReview, $"Requiere revisión humana: {reason}", null, now);
    }

    public void RecordTriageReview(bool accepted, Guid reviewerId, DateTimeOffset now) =>
        Log(TicketActivityType.TriageReviewed, accepted ? "Triage de IA aceptado." : "Triage de IA corregido por un agente.", reviewerId, now);

    public void SetTags(IEnumerable<string> tags) => _tags = Guard.NormalizeTags(tags);

    /// <summary>Evalúa incumplimientos de SLA. Devuelve true si se marcó alguno nuevo.</summary>
    public bool EvaluateSla(DateTimeOffset now)
    {
        var changed = false;

        if (!FirstResponseBreached && FirstRespondedAt is null && FirstResponseDueAt is { } frDue && now > frDue
            && Status is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            FirstResponseBreached = true;
            changed = true;
            Log(TicketActivityType.SlaBreached, "SLA de primera respuesta incumplido.", null, now);
            Raise(new TicketSlaBreached(TenantId, Id, "first_response", now));
        }

        if (!ResolutionBreached && SlaPausedAt is null && ResolutionDueAt is { } resDue && now > resDue
            && !TicketStateMachine.PausesSla(Status))
        {
            ResolutionBreached = true;
            changed = true;
            Log(TicketActivityType.SlaBreached, "SLA de resolución incumplido.", null, now);
            Raise(new TicketSlaBreached(TenantId, Id, "resolution", now));
        }

        return changed;
    }

    public void SoftDelete(DateTimeOffset now)
    {
        IsDeleted = true;
        DeletedAt = now;
    }

    private void RecalculateSla(SlaPolicy? slaPolicy)
    {
        if (slaPolicy is null)
        {
            FirstResponseDueAt = null;
            ResolutionDueAt = null;
            return;
        }

        var target = slaPolicy.TargetFor(Priority);
        if (FirstRespondedAt is null)
        {
            FirstResponseDueAt = CreatedAt + target.FirstResponse;
        }

        ResolutionDueAt = CreatedAt + target.Resolution + SlaPausedDuration;
    }

    /// <summary>Pausa el reloj de resolución al entrar en Pending/Resolved y lo reanuda al salir.</summary>
    private void UpdateSlaClock(TicketStatus from, TicketStatus to, DateTimeOffset now)
    {
        var wasPaused = TicketStateMachine.PausesSla(from);
        var willPause = TicketStateMachine.PausesSla(to);

        if (!wasPaused && willPause)
        {
            SlaPausedAt = now;
        }
        else if (wasPaused && !willPause && SlaPausedAt is { } pausedAt)
        {
            var paused = now - pausedAt;
            SlaPausedDuration += paused;
            if (ResolutionDueAt is not null)
            {
                ResolutionDueAt += paused;
            }

            SlaPausedAt = null;
        }
    }

    private void EnsureNotClosed()
    {
        if (Status == TicketStatus.Closed)
        {
            throw new DomainException("El ticket está cerrado y no admite cambios.", "ticket_closed");
        }
    }

    private void Log(TicketActivityType type, string description, Guid? actorId, DateTimeOffset now) =>
        _activities.Add(new TicketActivity(Id, type, description, actorId, now));
}
