namespace SupportAI.Domain.Common;

/// <summary>
/// Raíz de agregado: único punto de entrada para modificar el agregado y origen de los eventos de dominio.
/// Los eventos se guardan en el outbox en la misma transacción (ver DomainEventsToOutboxInterceptor).
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
