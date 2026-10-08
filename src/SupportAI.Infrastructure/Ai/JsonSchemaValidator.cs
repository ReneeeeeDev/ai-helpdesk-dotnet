using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using SupportAI.Application.Abstractions.Ai;

namespace SupportAI.Infrastructure.Ai;

/// <summary>Validación con JsonSchema.Net. Los esquemas compilados se cachean por contenido.</summary>
internal sealed class JsonSchemaValidator : IJsonSchemaValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _cache = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Validate(string json, string schema)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"JSON mal formado: {ex.Message}"];
        }

        using var _ = document;
        var compiled = _cache.GetOrAdd(schema, static s => JsonSchema.FromText(s));
        var result = compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }

        return (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{(string.IsNullOrEmpty(d.InstanceLocation.ToString()) ? "$" : d.InstanceLocation.ToString())}: {e.Value}"))
            .Distinct()
            .DefaultIfEmpty("El JSON no cumple el esquema.")
            .ToList();
    }
}
