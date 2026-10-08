using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Domain.Ai;

namespace SupportAI.Infrastructure.Ai.Claude;

/// <summary>
/// Implementación de <see cref="ILlmClient"/> con el SDK oficial de Anthropic para C#.
/// <list type="bullet">
/// <item>Endpoint beta para habilitar el fallback del lado del servidor ante rechazos (<c>stop_reason = refusal</c>).</item>
/// <item>Thinking adaptativo (siempre activo en claude-opus-5-5); la profundidad se controla con <c>output_config.effort</c>.</item>
/// <item>Salidas estructuradas con <c>output_config.format</c> (JSON Schema) — sin prefill, que el modelo no admite.</item>
/// <item>Prompt caching del system prompt (bloque estático con <c>cache_control: ephemeral</c>).</item>
/// <item>Tool use con herramientas <c>strict</c> y <c>tool_choice: auto</c> (el modo forzado devuelve 400 en este modelo).</item>
/// </list>
/// </summary>
internal sealed partial class ClaudeLlmClient(AnthropicClient client, IOptions<LlmOptions> options, ILogger<ClaudeLlmClient> logger) : ILlmClient
{
    public string ProviderName => "claude";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var parameters = BuildParameters(request, ToMessageParams(request.Messages), tools: null);
        var start = Stopwatch.GetTimestamp();
        var message = await SendAsync(parameters, ct);
        return ToResponse(message, Stopwatch.GetElapsedTime(start));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = BuildParameters(request, ToMessageParams(request.Messages), tools: null);
        var start = Stopwatch.GetTimestamp();
        var text = new StringBuilder();
        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
        string model = request.Model;
        string stopReason = LlmStopReasons.EndTurn;

        await using var enumerator = client.Beta.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            BetaRawMessageStreamEvent streamEvent;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                streamEvent = enumerator.Current;
            }
            catch (Exception ex) when (MapException(ex, ct) is { } mapped)
            {
                throw mapped;
            }

            if (streamEvent.TryPickStart(out var started))
            {
                model = started.Message.Model.ToString() ?? model;
                input = started.Message.Usage.InputTokens;
                cacheWrite = started.Message.Usage.CacheCreationInputTokens ?? 0;
                cacheRead = started.Message.Usage.CacheReadInputTokens ?? 0;
            }
            else if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var textDelta))
            {
                text.Append(textDelta.Text);
                yield return new LlmStreamChunk(textDelta.Text, null);
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                output = messageDelta.Usage.OutputTokens;
                if (messageDelta.Delta.StopReason is { } reason)
                {
                    stopReason = reason.ToString() ?? stopReason;
                }
            }
        }

        var usage = new LlmUsage(input, output, cacheWrite, cacheRead);
        yield return new LlmStreamChunk(null, new LlmResponse(text.ToString(), stopReason, usage, model, Stopwatch.GetElapsedTime(start)));
    }

    public async IAsyncEnumerable<LlmAgentEvent> RunAgentAsync(
        LlmRequest request, IReadOnlyList<LlmToolDefinition> tools, LlmToolExecutor executor, int maxIterations, [EnumeratorCancellation] CancellationToken ct)
    {
        var toolParams = tools.Select(ToTool).ToList();
        var messages = ToMessageParams(request.Messages);
        var usage = LlmUsage.Empty;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var message = await SendAsync(BuildParameters(request, messages, toolParams), ct);
            var response = ToResponse(message, TimeSpan.Zero);
            usage += response.Usage;

            if (response.IsRefusal)
            {
                throw new LlmException(LlmErrorKind.Refused, $"El modelo rechazó la solicitud ({response.RefusalCategory ?? "sin categoría"}).");
            }

            // Historial append-only: el turno del asistente se devuelve íntegro (thinking con firma incluido).
            var assistantBlocks = new List<BetaContentBlockParam>();
            var toolUses = new List<BetaToolUseBlock>();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistantBlocks.Add(new BetaTextBlockParam { Text = text.Text });
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    assistantBlocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistantBlocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistantBlocks.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    toolUses.Add(toolUse);
                }
            }

            if (response.StopReason != LlmStopReasons.ToolUse || toolUses.Count == 0)
            {
                yield return new LlmAgentEvent("text", response.Text);
                yield return new LlmAgentEvent("done", Usage: usage, Iterations: iteration);
                yield break;
            }

            messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistantBlocks });

            foreach (var toolUse in toolUses)
            {
                yield return new LlmAgentEvent("tool_call", JsonSerializer.Serialize(toolUse.Input), toolUse.Name);
            }

            // Herramientas independientes en paralelo; todos los tool_result van en UN único mensaje de usuario.
            var results = await Task.WhenAll(toolUses.Select(async tu =>
            {
                var input = JsonSerializer.SerializeToElement(tu.Input);
                var result = await executor(tu.Name, input, ct);
                return (BetaContentBlockParam)new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = result.Content, IsError = result.IsError };
            }));

            messages.Add(new BetaMessageParam { Role = Role.User, Content = results.ToList() });
        }

        LogMaxIterations(logger, maxIterations);
        yield return new LlmAgentEvent("text", "No pude completar la respuesta en el número máximo de pasos. Reformula la pregunta de forma más concreta.");
        yield return new LlmAgentEvent("done", Usage: usage, Iterations: maxIterations);
    }

    private MessageCreateParams BuildParameters(LlmRequest request, List<BetaMessageParam> messages, List<BetaToolUnion>? tools)
    {
        var settings = options.Value;
        var outputConfig = new BetaOutputConfig
        {
            Effort = request.Effort switch
            {
                "low" => Effort.Low,
                "high" => Effort.High,
                "xhigh" => Effort.Xhigh,
                "max" => Effort.Max,
                _ => Effort.Medium,
            }
        };

        if (request.JsonSchema is not null)
        {
            outputConfig = outputConfig with
            {
                Format = new BetaJsonOutputFormat
                {
                    Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.JsonSchema)!,
                },
            };
        }

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new()
                {
                    Text = request.SystemPrompt,
                    CacheControl = request.CacheSystemPrompt ? new BetaCacheControlEphemeral() : null,
                },
            },
            Messages = messages,
            OutputConfig = outputConfig,
        };

        if (tools is { Count: > 0 })
        {
            parameters = parameters with { Tools = tools, ToolChoice = new BetaToolChoiceAuto() };
        }

        if (!string.IsNullOrWhiteSpace(settings.ServerSideFallback))
        {
            parameters = parameters with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                // "default": Anthropic elige el modelo de respaldo según la categoría del rechazo.
                Fallbacks = new Default(),
            };
        }

        return parameters;
    }

    private async Task<BetaMessage> SendAsync(MessageCreateParams parameters, CancellationToken ct)
    {
        try
        {
            return await client.Beta.Messages.Create(parameters, ct);
        }
        catch (Exception ex) when (MapException(ex, ct) is { } mapped)
        {
            LogProviderError(logger, mapped.Kind, ex.Message);
            throw mapped;
        }
    }

    private static List<BetaMessageParam> ToMessageParams(IReadOnlyList<LlmMessage> messages) =>
        messages.Select(m => new BetaMessageParam
        {
            Role = m.Role == LlmRole.Assistant ? Role.Assistant : Role.User,
            Content = m.Content,
        }).ToList();

    private static BetaToolUnion ToTool(LlmToolDefinition tool)
    {
        // strict: true exige additionalProperties=false y required completo; se envía el esquema crudo tal cual.
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.InputSchemaJson)!;
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            Strict = true,
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    private static LlmResponse ToResponse(BetaMessage message, TimeSpan latency)
    {
        var text = string.Concat(message.Content.Select(b => b.TryPickText(out var t) ? t.Text : string.Empty));
        var usage = new LlmUsage(
            message.Usage.InputTokens,
            message.Usage.OutputTokens,
            message.Usage.CacheCreationInputTokens ?? 0,
            message.Usage.CacheReadInputTokens ?? 0);
        var stopReason = message.StopReason?.ToString() ?? LlmStopReasons.EndTurn;
        var refusalCategory = message.StopDetails?.Category?.ToString();
        return new LlmResponse(text, stopReason, usage, message.Model.ToString() ?? string.Empty, latency, refusalCategory);
    }

    /// <summary>Errores del SDK → <see cref="LlmException"/> clasificada (del más específico al más general).</summary>
    private static LlmException? MapException(Exception ex, CancellationToken ct) => ex switch
    {
        LlmException => null,
        AnthropicRateLimitException => new LlmException(LlmErrorKind.RateLimited, "Límite de solicitudes del proveedor de IA alcanzado.", ex),
        Anthropic5xxException => new LlmException(LlmErrorKind.Overloaded, "El proveedor de IA está sobrecargado o no disponible.", ex),
        AnthropicUnauthorizedException or AnthropicForbiddenException => new LlmException(LlmErrorKind.Authentication, "Credenciales de IA inválidas o sin permisos.", ex),
        AnthropicBadRequestException or AnthropicUnprocessableEntityException or AnthropicNotFoundException =>
            new LlmException(LlmErrorKind.InvalidRequest, $"Solicitud inválida al proveedor de IA: {ex.Message}", ex),
        AnthropicIOException => new LlmException(LlmErrorKind.Unavailable, "Error de red con el proveedor de IA.", ex),
        OperationCanceledException when !ct.IsCancellationRequested => new LlmException(LlmErrorKind.Timeout, "La solicitud al proveedor de IA superó el tiempo límite.", ex),
        AnthropicApiException => new LlmException(LlmErrorKind.Unknown, $"Error del proveedor de IA: {ex.Message}", ex),
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error del proveedor Claude ({Kind}): {Message}")]
    private static partial void LogProviderError(ILogger logger, LlmErrorKind kind, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El copiloto alcanzó el máximo de {MaxIterations} iteraciones de herramientas")]
    private static partial void LogMaxIterations(ILogger logger, int maxIterations);
}
