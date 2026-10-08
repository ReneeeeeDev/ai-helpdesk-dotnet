namespace SupportAI.Application.Abstractions.Tenancy;

/// <summary>
/// Tenant de la operación en curso. En requests HTTP se toma del JWT; en procesos en segundo plano
/// (outbox, SLA) se fija explícitamente con <see cref="ITenantScope.Use"/>.
/// Si no hay tenant, los filtros globales no devuelven filas ("fail closed").
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    bool HasTenant => TenantId is not null;

    Guid RequiredTenantId => TenantId ?? throw new InvalidOperationException("No hay un tenant establecido para esta operación.");
}

/// <summary>Permite fijar el tenant del scope actual (middleware HTTP, workers).</summary>
public interface ITenantScope
{
    void Use(Guid tenantId);
}
