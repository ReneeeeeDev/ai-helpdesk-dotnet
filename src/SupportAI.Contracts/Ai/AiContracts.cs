using SupportAI.Contracts.Common;

namespace SupportAI.Contracts.Ai;

public sealed record TriageResultDto(
    Guid Id,
    string? Category,
    TicketPriority Priority,
    Sentiment? Sentiment,
    string? Language,
    string? Summary,
    string? SuggestedTeam,
    Guid? SuggestedTeamId,
    IReadOnlyList<string> Tags,
    decimal Confidence,
    bool RequiresHuman,
    bool ContainsPii,
    string? Reasoning,
    TriageResultStatus Status,
    string PromptKey,
    int PromptVersion,
    string Model,
    string? ValidationErrors,
    DateTimeOffset CreatedAt);

public sealed record OverrideTriageRequest(string? Category, TicketPriority Priority, Guid? TeamId, string? Notes);

public sealed record CitationDto(Guid ArticleId, string Title);

public sealed record SuggestedReplyDto(
    Guid Id,
    string Body,
    IReadOnlyList<CitationDto> Citations,
    IReadOnlyList<string> FollowUpQuestions,
    decimal Confidence,
    bool ShouldEscalate,
    DateTimeOffset CreatedAt);

public sealed record CopilotRequest(string Question, Guid? TicketId);

/// <summary>Evento del stream SSE del copiloto: text (fragmento), tool (herramienta usada), done o error.</summary>
public sealed record CopilotStreamEvent(string Type, string? Text, string? ToolName = null);

public sealed record AiUsageDayDto(
    DateOnly Day,
    string Operation,
    int Calls,
    int Failures,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    decimal CostUsd,
    double AvgLatencyMs);

public sealed record PromptAccuracyDto(
    string PromptKey,
    int PromptVersion,
    int Total,
    int AutoApplied,
    int Accepted,
    int Overridden,
    int Invalid,
    double AcceptanceRate);

public sealed record AiUsageReportDto(
    DateOnly From,
    DateOnly To,
    long TokensThisMonth,
    long MonthlyTokenBudget,
    decimal CostThisMonthUsd,
    double CacheHitRatio,
    IReadOnlyList<AiUsageDayDto> Days,
    IReadOnlyList<PromptAccuracyDto> PromptAccuracy);

public sealed record PromptTemplateDto(
    Guid Id,
    string Key,
    int Version,
    string Description,
    string Model,
    string Effort,
    int MaxTokens,
    bool IsActive,
    bool HasSchema,
    string ContentHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record PromptTemplateDetailDto(PromptTemplateDto Info, string SystemPrompt, string UserTemplate, string? JsonSchema);
