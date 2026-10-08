namespace SupportAI.Domain.Common;

/// <summary>Entidad que pertenece a un tenant. EF Core aplica un filtro global por TenantId.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

/// <summary>Entidad con marcas de auditoría. Las asigna un interceptor de EF Core usando TimeProvider.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }

    DateTimeOffset? UpdatedAt { get; }
}

/// <summary>Entidad con borrado lógico. EF Core la excluye de las consultas mediante un filtro global.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAt { get; }
}
