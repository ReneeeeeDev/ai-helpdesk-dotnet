using System.Text.Json;
using SupportAI.Domain.Ai;

namespace SupportAI.Application.Abstractions.Ai;

public enum LlmRole
{
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>Solicitud al modelo, independiente del proveedor.</summary>
public sealed record LlmRequest
{
    /// <summary>triage | reply | summary | copilot | eval | review (para métricas y logs).</summary>
    public required string Operation { get; init; }

    public required string Model { get; init; }

    /// <summary>low | medium | high | xhigh | max.</summary>
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }

    /// <summary>Instrucciones estáticas (se cachean con prompt caching: deben ser idénticas entre llamadas).</summary>
    public required string SystemPrompt { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    /// <summary>Si se informa, se pide salida estructurada (JSON que cumple este JSON Schema).</summary>
    public string? JsonSchema { get; init; }

    public bool CacheSystemPrompt { get; init; } = true;
}

public sealed record LlmResponse(string Text, string StopReason, LlmUsage Usage, string Model, TimeSpan Latency, string? RefusalCategory = null)
{
    public bool IsRefusal => StopReason == LlmStopReasons.Refusal;

    public bool IsTruncated => StopReason == LlmStopReasons.MaxTokens;
}

public static class LlmStopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string ToolUse = "tool_use";
    public const string Refusal = "refusal";
}

/// <summary>Fragmento de un stream: delta de texto o, al final, la respuesta completa con el uso de tokens.</summary>
public sealed record LlmStreamChunk(string? TextDelta, LlmResponse? Final);

/// <summary>Herramienta que el modelo puede invocar (tool use). El esquema de entrada es JSON Schema.</summary>
public sealed record LlmToolDefinition(string Name, string Description, string InputSchemaJson);

public delegate Task<LlmToolResult> LlmToolExecutor(string toolName, JsonElement input, CancellationToken ct);

public sealed record LlmToolResult(string Content, bool IsError = false);

/// <summary>Evento del bucle agéntico: tool_call, text, done.</summary>
public sealed record LlmAgentEvent(string Type, string? Text = null, string? ToolName = null, LlmUsage? Usage = null, int Iterations = 0);

/// <summary>Puerto hacia el proveedor de LLM (Claude en producción, Fake en desarrollo y tests).</summary>
public interface ILlmClient
{
    string ProviderName { get; }

    /// <exception cref="LlmException">Errores del proveedor clasificados (transitorios o no).</exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);

    IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, CancellationToken ct);

    /// <summary>Bucle de tool use: el modelo pide herramientas, se ejecutan con <paramref name="executor"/> y se repite hasta end_turn.</summary>
    IAsyncEnumerable<LlmAgentEvent> RunAgentAsync(
        LlmRequest request, IReadOnlyList<LlmToolDefinition> tools, LlmToolExecutor executor, int maxIterations, CancellationToken ct);
}

public enum LlmErrorKind
{
    RateLimited,
    Overloaded,
    Unavailable,
    Timeout,
    InvalidRequest,
    Authentication,
    Refused,
    Unknown,
}

/// <summary>Error del proveedor normalizado. <see cref="IsTransient"/> decide si el outbox debe reintentar.</summary>
public sealed class LlmException(LlmErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
    : Exception(message, inner)
{
    public LlmErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsTransient => Kind is LlmErrorKind.RateLimited or LlmErrorKind.Overloaded or LlmErrorKind.Unavailable or LlmErrorKind.Timeout;
}
