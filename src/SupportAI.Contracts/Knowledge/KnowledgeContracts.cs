namespace SupportAI.Contracts.Knowledge;

public sealed record KnowledgeArticleSummaryDto(
    Guid Id,
    string Title,
    string? Category,
    IReadOnlyList<string> Tags,
    bool IsPublished,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record KnowledgeArticleDto(
    Guid Id,
    string Title,
    string Body,
    string? Category,
    IReadOnlyList<string> Tags,
    bool IsPublished,
    Guid AuthorId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record UpsertArticleRequest(string Title, string Body, string? Category, IReadOnlyList<string>? Tags, bool IsPublished);
