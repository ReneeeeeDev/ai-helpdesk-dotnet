namespace SupportAI.Infrastructure.Ai;

public enum LlmProvider
{
    /// <summary>Respuestas deterministas locales: desarrollo sin API key y todos los tests.</summary>
    Fake,

    /// <summary>Claude (Anthropic) mediante el SDK oficial.</summary>
    Claude,
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Fake;

    /// <summary>API key. Si está vacía, el SDK usa ANTHROPIC_API_KEY / el perfil de <c>ant auth login</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Timeout por intento (las peticiones con mucho output deberían usar streaming).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Reintentos del SDK ante 408/409/429/5xx y errores de red (con backoff propio del SDK).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Fallback del lado del servidor si el modelo rechaza la solicitud: "default" (Anthropic elige el modelo según la
    /// categoría del rechazo) o vacío para desactivarlo.
    /// </summary>
    public string? ServerSideFallback { get; set; } = "default";

    /// <summary>Circuit breaker: proporción de fallos transitorios que abre el circuito.</summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    public int CircuitBreakerMinimumThroughput { get; set; } = 6;

    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Latencia simulada por el proveedor Fake (para ver los estados de carga en la UI).</summary>
    public TimeSpan FakeLatency { get; set; } = TimeSpan.FromMilliseconds(300);
}
