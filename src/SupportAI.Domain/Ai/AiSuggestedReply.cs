using SupportAI.Domain.Common;

namespace SupportAI.Domain.Ai;

public enum SuggestedReplyStatus
{
    Draft = 0,
    Used = 1,
    Discarded = 2,
}

/// <summary>Borrador de respuesta generado por IA con citas a artículos de la base de conocimiento.</summary>
public sealed class AiSuggestedReply : AggregateRoot, ITenantOwned
{
    private List<Guid> _citedArticleIds = [];
    private List<string> _followUpQuestions = [];

    private AiSuggestedReply()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public int PromptVersion { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public IReadOnlyList<Guid> CitedArticleIds => _citedArticleIds.AsReadOnly();

    public IReadOnlyList<string> FollowUpQuestions => _followUpQuestions.AsReadOnly();

    public decimal Confidence { get; private set; }

    public bool ShouldEscalate { get; private set; }

    public SuggestedReplyStatus Status { get; private set; }

    public Guid? RequestedById { get; private set; }

    public Guid? LlmCallId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AiSuggestedReply Create(
        Guid tenantId,
        Guid ticketId,
        int promptVersion,
        string body,
        IEnumerable<Guid> citedArticleIds,
        IEnumerable<string> followUpQuestions,
        decimal confidence,
        bool shouldEscalate,
        Guid? requestedById,
        Guid? llmCallId,
        DateTimeOffset now) =>
        new()
        {
            Id = NewId(),
            TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)),
            TicketId = Guard.NotEmpty(ticketId, nameof(ticketId)),
            PromptVersion = promptVersion,
            Body = Guard.NotNullOrWhiteSpace(body, nameof(body), 20_000),
            _citedArticleIds = citedArticleIds.Distinct().ToList(),
            _followUpQuestions = followUpQuestions.Where(q => !string.IsNullOrWhiteSpace(q)).Take(5).ToList(),
            Confidence = Math.Clamp(confidence, 0m, 1m),
            ShouldEscalate = shouldEscalate,
            RequestedById = requestedById,
            LlmCallId = llmCallId,
            Status = SuggestedReplyStatus.Draft,
            CreatedAt = now,
        };

    public void MarkUsed()
    {
        EnsureDraft();
        Status = SuggestedReplyStatus.Used;
    }

    public void Discard()
    {
        EnsureDraft();
        Status = SuggestedReplyStatus.Discarded;
    }

    private void EnsureDraft()
    {
        if (Status != SuggestedReplyStatus.Draft)
        {
            throw new DomainException("La respuesta sugerida ya fue utilizada o descartada.", "reply_not_draft");
        }
    }
}
