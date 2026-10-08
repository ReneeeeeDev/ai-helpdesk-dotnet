namespace SupportAI.Domain.Common;

/// <summary>Hecho relevante del negocio que ya ocurrió. Inmutable y serializable (viaja por el outbox).</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }

    Guid TenantId { get; }
}

/// <summary>Base para eventos de dominio con identidad y marca de tiempo.</summary>
public abstract record DomainEvent(Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
}
