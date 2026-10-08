namespace SupportAI.Domain.Tickets;

public enum TicketStatus
{
    New = 0,
    Triaged = 1,
    Open = 2,
    Pending = 3,
    Resolved = 4,
    Closed = 5,
}

/// <summary>Orden ascendente de severidad (permite comparar con &lt; / &gt;).</summary>
public enum TicketPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}

public enum TicketChannel
{
    Web = 0,
    Email = 1,
    Api = 2,
    Chat = 3,
}

public enum Sentiment
{
    VeryNegative = 0,
    Negative = 1,
    Neutral = 2,
    Positive = 3,
}

public enum MessageAuthorType
{
    Customer = 0,
    Agent = 1,
    Ai = 2,
    System = 3,
}
