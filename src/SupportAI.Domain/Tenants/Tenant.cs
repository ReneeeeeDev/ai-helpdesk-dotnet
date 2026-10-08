using SupportAI.Domain.Common;

namespace SupportAI.Domain.Tenants;

/// <summary>Organización cliente de la plataforma. Define su taxonomía de categorías y su configuración de IA.</summary>
public sealed class Tenant : AggregateRoot, IAuditable
{
    public const int MaxCategories = 30;

    private List<string> _categories = [];

    private Tenant()
    {
        AiSettings = TenantAiSettings.Default;
    }

    private Tenant(Guid id, string name, string slug, IEnumerable<string> categories, TenantAiSettings aiSettings)
        : base(id)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 120);
        Slug = NormalizeSlug(slug);
        SetCategories(categories);
        AiSettings = aiSettings;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Identificador legible y único (p.ej. "acme").</summary>
    public string Slug { get; private set; } = string.Empty;

    /// <summary>Taxonomía de categorías que el triage con IA debe respetar.</summary>
    public IReadOnlyList<string> Categories => _categories.AsReadOnly();

    public TenantAiSettings AiSettings { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static Tenant Create(string name, string slug, IEnumerable<string> categories, TenantAiSettings? aiSettings = null) =>
        new(NewId(), name, slug, categories, aiSettings ?? TenantAiSettings.Default);

    public void Rename(string name) => Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 120);

    public void SetCategories(IEnumerable<string> categories)
    {
        var normalized = categories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
        {
            throw new DomainException("El tenant debe tener al menos una categoría.", "tenant_categories_required");
        }

        if (normalized.Count > MaxCategories)
        {
            throw new DomainException($"Máximo {MaxCategories} categorías por tenant.", "tenant_categories_limit");
        }

        _categories = normalized;
    }

    public bool HasCategory(string? category) =>
        category is not null && _categories.Contains(category.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public void UpdateAiSettings(TenantAiSettings settings) => AiSettings = settings;

    private static string NormalizeSlug(string slug)
    {
        var value = Guard.NotNullOrWhiteSpace(slug, nameof(slug), 60).ToLowerInvariant();
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException("El slug solo admite letras, dígitos y guiones.", "tenant_slug_invalid");
        }

        return value;
    }
}
