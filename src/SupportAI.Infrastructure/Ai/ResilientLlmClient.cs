using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using SupportAI.Application.Abstractions.Ai;

namespace SupportAI.Infrastructure.Ai;

/// <summary>
/// Decorador con circuit breaker (Polly v8). El SDK ya reintenta errores transitorios; si aun así el proveedor sigue
/// fallando, el circuito se abre y las llamadas fallan rápido durante un tiempo en lugar de acumular latencia.
/// El outbox reintentará el triage más tarde; los endpoints devuelven 503.
/// </summary>
internal sealed partial class ResilientLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly CircuitBreakerStateProvider _state = new();
    private readonly TimeSpan _breakDuration;

    public ResilientLlmClient(ILlmClient inner, IOptions<LlmOptions> options, ILogger<ResilientLlmClient> logger)
    {
        _inner = inner;
        var settings = options.Value;
        _breakDuration = settings.CircuitBreakerBreakDuration;
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = settings.CircuitBreakerFailureRatio,
                MinimumThroughput = settings.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromMinutes(1),
                BreakDuration = settings.CircuitBreakerBreakDuration,
                StateProvider = _state,
                ShouldHandle = new PredicateBuilder().Handle<LlmException>(e => e.IsTransient),
                OnOpened = args =>
                {
                    LogCircuitOpened(logger, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    LogCircuitClosed(logger);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public string ProviderName => _inner.ProviderName;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async token => await _inner.CompleteAsync(request, token), ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw Unavailable(ex);
        }
    }

    public IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, CancellationToken ct)
    {
        EnsureClosed();
        return _inner.StreamAsync(request, ct);
    }

    public async IAsyncEnumerable<LlmAgentEvent> RunAgentAsync(
        LlmRequest request, IReadOnlyList<LlmToolDefinition> tools, LlmToolExecutor executor, int maxIterations, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureClosed();
        await foreach (var item in _inner.RunAgentAsync(request, tools, executor, maxIterations, ct))
        {
            yield return item;
        }
    }

    private void EnsureClosed()
    {
        if (_state.CircuitState is CircuitState.Open or CircuitState.Isolated)
        {
            throw Unavailable(null);
        }
    }

    private LlmException Unavailable(Exception? inner) =>
        new(LlmErrorKind.Unavailable, "El proveedor de IA está temporalmente deshabilitado por fallos repetidos (circuit breaker abierto).", inner, _breakDuration);

    [LoggerMessage(Level = LogLevel.Error, Message = "Circuit breaker del LLM ABIERTO durante {Seconds} s")]
    private static partial void LogCircuitOpened(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker del LLM cerrado: el proveedor responde de nuevo")]
    private static partial void LogCircuitClosed(ILogger logger);
}
