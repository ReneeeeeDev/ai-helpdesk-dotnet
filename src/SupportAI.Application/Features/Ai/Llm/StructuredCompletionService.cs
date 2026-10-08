using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Domain.Ai;

namespace SupportAI.Application.Features.Ai.Llm;

public sealed record StructuredResult<T>(
    T? Value,
    bool IsValid,
    IReadOnlyList<string> Errors,
    string RawJson,
    Guid? LlmCallId,
    int Attempts,
    LlmUsage Usage)
    where T : class;

/// <summary>
/// Pipeline de salida estructurada:
/// <list type="number">
/// <item>Llamada con <c>output_config.format</c> = JSON Schema (la salida cumple el esquema por construcción).</item>
/// <item>Gestión de <c>stop_reason</c>: <c>refusal</c> → error, <c>max_tokens</c> → reintento con más margen.</item>
/// <item>Validación JSON Schema local (defensa en profundidad; también cubre al proveedor Fake).</item>
/// <item>Deserialización a un record de C# y validación semántica (reglas de negocio).</item>
/// <item>Si falla, un reintento de "reparación" con los errores como feedback.</item>
/// </list>
/// Cada llamada queda registrada en LlmCallLog.
/// </summary>
public sealed partial class StructuredCompletionService(
    ILlmClient llm,
    IJsonSchemaValidator schemaValidator,
    ILlmCallRecorder recorder,
    ILogger<StructuredCompletionService> logger)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private const int MaxRepairAttempts = 1;

    public async Task<StructuredResult<T>> CompleteAsync<T>(
        PromptDefinition prompt,
        string userMessage,
        LlmCallContext context,
        Func<T, IEnumerable<string>> semanticValidator,
        CancellationToken ct,
        Func<JsonObject, JsonObject>? schemaCustomizer = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(prompt.JsonSchema, nameof(prompt.JsonSchema));

        var schema = schemaCustomizer is null
            ? prompt.JsonSchema
            : schemaCustomizer(JsonNode.Parse(prompt.JsonSchema)!.AsObject()).ToJsonString();

        var messages = new List<LlmMessage> { new(LlmRole.User, userMessage) };
        var maxTokens = prompt.MaxTokens;
        var totalUsage = LlmUsage.Empty;
        Guid? lastCallId = null;
        string raw = string.Empty;
        IReadOnlyList<string> errors = [];

        for (var attempt = 1; attempt <= MaxRepairAttempts + 2; attempt++)
        {
            var request = new LlmRequest
            {
                Operation = context.Operation,
                Model = prompt.Model,
                Effort = prompt.Effort,
                MaxTokens = maxTokens,
                SystemPrompt = prompt.SystemPrompt,
                Messages = messages,
                JsonSchema = schema,
            };

            var response = await CallAndRecordAsync(request, context, attempt, ct);
            totalUsage += response.Usage;
            lastCallId = response.CallId;

            if (response.Response.IsRefusal)
            {
                throw new LlmException(LlmErrorKind.Refused, $"El modelo rechazó la solicitud ({response.Response.RefusalCategory ?? "sin categoría"}).");
            }

            if (response.Response.IsTruncated)
            {
                // La respuesta se cortó (el thinking también consume max_tokens): un reintento con el doble de margen.
                if (maxTokens >= 32_000)
                {
                    errors = ["La respuesta del modelo se truncó por max_tokens."];
                    break;
                }

                LogTruncated(logger, context.Operation, maxTokens);
                maxTokens *= 2;
                continue;
            }

            raw = response.Response.Text.Trim();
            (var value, errors) = Validate(raw, schema, semanticValidator);
            if (errors.Count == 0)
            {
                return new StructuredResult<T>(value, true, [], raw, lastCallId, attempt, totalUsage);
            }

            LogInvalidOutput(logger, context.Operation, attempt, string.Join(" | ", errors));
            if (attempt > MaxRepairAttempts)
            {
                break;
            }

            // Reparación: se devuelve al modelo su salida y la lista de errores (turno de asistente + turno de usuario,
            // no es prefill: el último mensaje sigue siendo del usuario).
            messages =
            [
                .. messages,
                new LlmMessage(LlmRole.Assistant, raw.Length == 0 ? "{}" : raw),
                new LlmMessage(LlmRole.User,
                    "Tu respuesta anterior no es válida por estos motivos:\n- " + string.Join("\n- ", errors) +
                    "\nDevuelve de nuevo el JSON completo y corregido, respetando el esquema y las reglas."),
            ];
        }

        return new StructuredResult<T>(null, false, errors, raw, lastCallId, MaxRepairAttempts + 1, totalUsage);
    }

    private (T? Value, IReadOnlyList<string> Errors) Validate<T>(string raw, string schema, Func<T, IEnumerable<string>> semanticValidator)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, ["La respuesta está vacía."]);
        }

        var schemaErrors = schemaValidator.Validate(raw, schema);
        if (schemaErrors.Count > 0)
        {
            return (null, schemaErrors);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, [$"JSON no deserializable: {ex.Message}"]);
        }

        if (value is null)
        {
            return (null, ["El JSON es null."]);
        }

        var semanticErrors = semanticValidator(value).ToList();
        return (value, semanticErrors);
    }

    private async Task<(LlmResponse Response, Guid CallId, LlmUsage Usage)> CallAndRecordAsync(LlmRequest request, LlmCallContext context, int attempt, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var response = await llm.CompleteAsync(request, ct);
            var callId = await recorder.RecordSuccessAsync(context, response, attempt, ct);
            return (response, callId, response.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await recorder.RecordFailureAsync(context, request.Model, ex, Stopwatch.GetElapsedTime(start), attempt, CancellationToken.None);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Salida de LLM truncada en {Operation} con max_tokens={MaxTokens}; se reintenta con el doble")]
    private static partial void LogTruncated(ILogger logger, string operation, int maxTokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Salida inválida de LLM en {Operation} (intento {Attempt}): {Errors}")]
    private static partial void LogInvalidOutput(ILogger logger, string operation, int attempt, string errors);
}
