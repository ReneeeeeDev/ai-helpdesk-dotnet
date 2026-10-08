using SupportAI.Application.Abstractions.Tenancy;

namespace SupportAI.Infrastructure.Tenancy;

/// <summary>Implementación scoped: una instancia por request o por unidad de trabajo en segundo plano.</summary>
internal sealed class TenantContext : ITenantContext, ITenantScope
{
    public Guid? TenantId { get; private set; }

    public void Use(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId vacío.", nameof(tenantId));
        }

        if (TenantId is not null && TenantId != tenantId)
        {
            // Evita mezclar datos de dos tenants en el mismo scope (y en el mismo DbContext).
            throw new InvalidOperationException("El tenant del scope ya fue establecido con otro valor.");
        }

        TenantId = tenantId;
    }
}
