using SupportAI.Domain.Common;

namespace SupportAI.Domain.Ai;

/// <summary>
/// Registro de cada llamada al LLM: base del dashboard de uso, del control de presupuesto y de la depuración.
/// Nunca guarda el contenido del prompt (puede contener datos del cliente), solo métricas.
/// </summary>
public sealed class LlmCallLog : Entity
{
    private LlmCallLog()
    {
    }

    public Guid? TenantId { get; private set; }

    public Guid? TicketId { get; private set; }

    /// <summary>triage | reply | summary | copilot | eval | review.</summary>
    public string Operation { get; private set; } = string.Empty;

    public string? PromptKey { get; private set; }

    public int? PromptVersion { get; private set; }

    public string Model { get; private set; } = string.Empty;

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheCreationInputTokens { get; private set; }

    public long CacheReadInputTokens { get; private set; }

    public long LatencyMs { get; private set; }

    public string? StopReason { get; private set; }

    public bool Succeeded { get; private set; }

    public string? ErrorType { get; private set; }

    public string? ErrorMessage { get; private set; }

    public decimal EstimatedCostUsd { get; private set; }

    public string? CorrelationId { get; private set; }

    public int Attempt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheCreationInputTokens + CacheReadInputTokens;

    public static LlmCallLog Success(
        Guid? tenantId, Guid? ticketId, string operation, string? promptKey, int? promptVersion, string model,
        LlmUsage usage, long latencyMs, string? stopReason, decimal costUsd, string? correlationId, int attempt, DateTimeOffset now) =>
        new()
        {
            Id = NewId(),
            TenantId = tenantId,
            TicketId = ticketId,
            Operation = operation,
            PromptKey = promptKey,
            PromptVersion = promptVersion,
            Model = model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheCreationInputTokens = usage.CacheCreationInputTokens,
            CacheReadInputTokens = usage.CacheReadInputTokens,
            LatencyMs = latencyMs,
            StopReason = stopReason,
            Succeeded = true,
            EstimatedCostUsd = costUsd,
            CorrelationId = correlationId,
            Attempt = attempt,
            CreatedAt = now,
        };

    public static LlmCallLog Failure(
        Guid? tenantId, Guid? ticketId, string operation, string? promptKey, int? promptVersion, string model,
        string errorType, string errorMessage, long latencyMs, string? correlationId, int attempt, DateTimeOffset now) =>
        new()
        {
            Id = NewId(),
            TenantId = tenantId,
            TicketId = ticketId,
            Operation = operation,
            PromptKey = promptKey,
            PromptVersion = promptVersion,
            Model = model,
            LatencyMs = latencyMs,
            Succeeded = false,
            ErrorType = errorType,
            ErrorMessage = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage,
            CorrelationId = correlationId,
            Attempt = attempt,
            CreatedAt = now,
        };
}

/// <summary>Uso de tokens reportado por el proveedor (value object).</summary>
public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheCreationInputTokens = 0, long CacheReadInputTokens = 0)
{
    public static LlmUsage Empty { get; } = new(0, 0);

    public long Total => InputTokens + OutputTokens + CacheCreationInputTokens + CacheReadInputTokens;

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheCreationInputTokens + b.CacheCreationInputTokens,
        a.CacheReadInputTokens + b.CacheReadInputTokens);

    public static LlmUsage Add(LlmUsage left, LlmUsage right) => left + right;
}
