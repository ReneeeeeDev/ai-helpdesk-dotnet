namespace SupportAI.Infrastructure.Persistence.Outbox;

public enum OutboxStatus
{
    Pending = 0,
    Processed = 1,
    DeadLetter = 2,
}

/// <summary>
/// Evento de dominio pendiente de procesar. Se inserta en la misma transacción que el cambio de estado
/// (patrón Transactional Outbox) y lo consume <see cref="OutboxProcessor"/>.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Nombre del tipo de evento (p.ej. "TicketCreated"), resuelto por <see cref="DomainEventTypeRegistry"/>.</summary>
    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }

    public string? CorrelationId { get; set; }
}
