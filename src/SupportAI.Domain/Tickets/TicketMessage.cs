using SupportAI.Domain.Common;

namespace SupportAI.Domain.Tickets;

/// <summary>Mensaje del hilo de un ticket. Las notas internas no son visibles para el cliente.</summary>
public sealed class TicketMessage : Entity
{
    public const int MaxBodyLength = 20_000;

    private TicketMessage()
    {
    }

    internal TicketMessage(Guid ticketId, Guid? authorId, MessageAuthorType authorType, string body, bool isInternal, DateTimeOffset createdAt)
        : base(NewId())
    {
        TicketId = ticketId;
        AuthorId = authorId;
        AuthorType = authorType;
        Body = Guard.NotNullOrWhiteSpace(body, nameof(body), MaxBodyLength);
        IsInternal = isInternal;
        CreatedAt = createdAt;
    }

    public Guid TicketId { get; private set; }

    /// <summary>Null para mensajes de sistema o de IA.</summary>
    public Guid? AuthorId { get; private set; }

    public MessageAuthorType AuthorType { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public bool IsInternal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
