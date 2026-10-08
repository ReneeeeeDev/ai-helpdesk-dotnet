using SupportAI.Application.Common.Mapping;
using SupportAI.Contracts.Ai;
using SupportAI.Domain.Ai;

namespace SupportAI.Application.Features.Ai;

internal static class AiMappings
{
    public static TriageResultDto ToDto(this AiTriageResult r) => new(
        r.Id,
        r.Category,
        r.Priority.ToContract(),
        r.Sentiment.ToContract(),
        r.Language,
        r.Summary,
        r.SuggestedTeam,
        r.SuggestedTeamId,
        r.Tags,
        r.Confidence,
        r.RequiresHuman,
        r.ContainsPii,
        r.Reasoning,
        r.Status.ToContract(),
        r.PromptKey,
        r.PromptVersion,
        r.Model,
        r.ValidationErrors,
        r.CreatedAt);

    public static PromptTemplateDto ToDto(this PromptTemplate p) => new(
        p.Id, p.Key, p.Version, p.Description, p.Model, p.Effort, p.MaxTokens, p.IsActive, p.JsonSchema is not null,
        p.ContentHash, p.CreatedAt, p.ActivatedAt);
}
