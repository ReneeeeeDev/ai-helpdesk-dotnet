using SupportAI.Domain.Common;

namespace SupportAI.Application.Abstractions.Events;

/// <summary>
/// Reacciona a un evento de dominio entregado por el outbox (at-least-once).
/// Los handlers DEBEN ser idempotentes: el mismo evento puede llegar más de una vez.
/// Se ejecutan en un scope propio con el tenant del evento ya establecido.
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
