using SupportAI.Domain.Common;

namespace SupportAI.Domain.Teams;

/// <summary>Grupo de agentes (p.ej. "Facturación", "Soporte técnico") al que se enrutan tickets.</summary>
public sealed class Team : AggregateRoot, ITenantOwned, IAuditable
{
    private List<string> _categories = [];

    private Team()
    {
    }

    private Team(Guid id, Guid tenantId, string name, string? description, IEnumerable<string> categories)
        : base(id)
    {
        TenantId = Guard.NotEmpty(tenantId, nameof(tenantId));
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 80);
        Description = description?.Trim();
        _categories = Guard.NormalizeTags(categories, max: 20, maxLength: 60);
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Categorías que atiende el equipo; se usan para el ruteo por defecto.</summary>
    public IReadOnlyList<string> Categories => _categories.AsReadOnly();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static Team Create(Guid tenantId, string name, string? description, IEnumerable<string> categories) =>
        new(NewId(), tenantId, name, description, categories);

    public void Update(string name, string? description, IEnumerable<string> categories)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 80);
        Description = description?.Trim();
        _categories = Guard.NormalizeTags(categories, max: 20, maxLength: 60);
    }

    public bool Handles(string? category) =>
        category is not null && _categories.Contains(category.Trim().ToLowerInvariant(), StringComparer.Ordinal);
}
