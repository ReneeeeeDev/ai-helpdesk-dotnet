using SupportAI.Domain.Common;
using SupportAI.Domain.Tickets;

namespace SupportAI.Domain.Ai;

public enum TriageResultStatus
{
    /// <summary>Confianza insuficiente: espera revisión de un agente.</summary>
    Suggested = 0,

    /// <summary>Aplicado automáticamente (confianza ≥ umbral del tenant).</summary>
    AutoApplied = 1,

    /// <summary>Un agente lo revisó y lo aceptó tal cual.</summary>
    Accepted = 2,

    /// <summary>Un agente lo corrigió. Señal negativa para medir la precisión del prompt.</summary>
    Overridden = 3,

    /// <summary>La salida no pasó la validación incluso tras el reintento de reparación.</summary>
    Invalid = 4,
}

/// <summary>
/// Resultado de un triage de IA. Se conserva aunque no se aplique: es el dataset para medir la precisión
/// de cada versión de prompt (Accepted vs Overridden).
/// </summary>
public sealed class AiTriageResult : AggregateRoot, ITenantOwned
{
    private List<string> _tags = [];

    private AiTriageResult()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public string PromptKey { get; private set; } = string.Empty;

    public int PromptVersion { get; private set; }

    public string Model { get; private set; } = string.Empty;

    public string? Category { get; private set; }

    public TicketPriority Priority { get; private set; }

    public Sentiment? Sentiment { get; private set; }

    public string? Language { get; private set; }

    public string? Summary { get; private set; }

    public string? SuggestedTeam { get; private set; }

    public Guid? SuggestedTeamId { get; private set; }

    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    public bool ContainsPii { get; private set; }

    public bool RequiresHuman { get; private set; }

    /// <summary>Confianza reportada por el modelo (0..1).</summary>
    public decimal Confidence { get; private set; }

    public string? Reasoning { get; private set; }

    /// <summary>JSON crudo devuelto por el modelo (auditoría / depuración).</summary>
    public string RawJson { get; private set; } = string.Empty;

    public TriageResultStatus Status { get; private set; }

    public string? ValidationErrors { get; private set; }

    public Guid? LlmCallId { get; private set; }

    public Guid? ReviewedById { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public string? ReviewNotes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AiTriageResult Create(
        Guid tenantId,
        Guid ticketId,
        string promptKey,
        int promptVersion,
        string model,
        TriageDecision decision,
        string? suggestedTeam,
        bool containsPii,
        bool requiresHuman,
        decimal confidence,
        string? reasoning,
        string rawJson,
        Guid? llmCallId,
        DateTimeOffset now) =>
        new()
        {
            Id = NewId(),
            TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)),
            TicketId = Guard.NotEmpty(ticketId, nameof(ticketId)),
            PromptKey = promptKey,
            PromptVersion = promptVersion,
            Model = model,
            Category = decision.Category,
            Priority = decision.Priority,
            Sentiment = decision.Sentiment,
            Language = decision.Language,
            Summary = decision.Summary,
            SuggestedTeam = suggestedTeam,
            SuggestedTeamId = decision.TeamId,
            _tags = Guard.NormalizeTags(decision.Tags),
            ContainsPii = containsPii,
            RequiresHuman = requiresHuman,
            Confidence = Math.Clamp(confidence, 0m, 1m),
            Reasoning = reasoning,
            RawJson = rawJson,
            LlmCallId = llmCallId,
            Status = TriageResultStatus.Suggested,
            CreatedAt = now,
        };

    /// <summary>Registra una salida que no pasó la validación (se guarda para análisis).</summary>
    public static AiTriageResult CreateInvalid(
        Guid tenantId, Guid ticketId, string promptKey, int promptVersion, string model, string rawJson, string errors, Guid? llmCallId, DateTimeOffset now) =>
        new()
        {
            Id = NewId(),
            TenantId = tenantId,
            TicketId = ticketId,
            PromptKey = promptKey,
            PromptVersion = promptVersion,
            Model = model,
            RawJson = rawJson,
            ValidationErrors = errors,
            LlmCallId = llmCallId,
            Status = TriageResultStatus.Invalid,
            RequiresHuman = true,
            Priority = TicketPriority.Normal,
            CreatedAt = now,
        };

    public TriageDecision ToDecision() => new(Category, Priority, Sentiment, Language, SuggestedTeamId, Tags, Summary);

    public void MarkAutoApplied()
    {
        EnsureStatus(TriageResultStatus.Suggested);
        Status = TriageResultStatus.AutoApplied;
    }

    public void Accept(Guid reviewerId, DateTimeOffset now)
    {
        EnsureReviewable();
        Status = TriageResultStatus.Accepted;
        ReviewedById = reviewerId;
        ReviewedAt = now;
    }

    public void Override(Guid reviewerId, string? notes, DateTimeOffset now)
    {
        EnsureReviewable();
        Status = TriageResultStatus.Overridden;
        ReviewedById = reviewerId;
        ReviewedAt = now;
        ReviewNotes = notes?.Trim();
    }

    private void EnsureReviewable()
    {
        if (Status is not (TriageResultStatus.Suggested or TriageResultStatus.AutoApplied))
        {
            throw new DomainException($"El resultado de triage ya fue revisado ({Status}).", "triage_already_reviewed");
        }
    }

    private void EnsureStatus(TriageResultStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Estado de triage inválido: se esperaba {expected} y es {Status}.", "triage_invalid_state");
        }
    }
}
