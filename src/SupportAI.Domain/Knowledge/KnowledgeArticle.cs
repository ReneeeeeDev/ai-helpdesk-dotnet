using SupportAI.Domain.Common;

namespace SupportAI.Domain.Knowledge;

/// <summary>Artículo de la base de conocimiento. Fuente de las citas en las respuestas sugeridas por IA.</summary>
public sealed class KnowledgeArticle : AggregateRoot, ITenantOwned, IAuditable, ISoftDeletable
{
    public const int MaxBodyLength = 50_000;

    private List<string> _tags = [];

    private KnowledgeArticle()
    {
    }

    public Guid TenantId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public string? Category { get; private set; }

    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    public bool IsPublished { get; private set; }

    public Guid AuthorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static KnowledgeArticle Create(Guid tenantId, Guid authorId, string title, string body, string? category, IEnumerable<string> tags, bool publish)
    {
        var article = new KnowledgeArticle
        {
            Id = NewId(),
            TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)),
            AuthorId = Guard.NotEmpty(authorId, nameof(authorId)),
        };
        article.Update(title, body, category, tags);
        article.IsPublished = publish;
        return article;
    }

    public void Update(string title, string body, string? category, IEnumerable<string> tags)
    {
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), 200);
        Body = Guard.NotNullOrWhiteSpace(body, nameof(body), MaxBodyLength);
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        _tags = Guard.NormalizeTags(tags);
    }

    public void Publish() => IsPublished = true;

    public void Unpublish() => IsPublished = false;

    public void SoftDelete(DateTimeOffset now)
    {
        IsDeleted = true;
        DeletedAt = now;
        IsPublished = false;
    }
}
