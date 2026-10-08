namespace SupportAI.Application.Abstractions.Ai;

/// <summary>Versión de prompt lista para usar (copia inmutable de <see cref="Domain.Ai.PromptTemplate"/>).</summary>
public sealed record PromptDefinition(
    string Key,
    int Version,
    string SystemPrompt,
    string UserTemplate,
    string? JsonSchema,
    string Model,
    string Effort,
    int MaxTokens);

public static class PromptKeys
{
    public const string Triage = "ticket.triage";
    public const string Reply = "ticket.reply";
    public const string Summary = "ticket.summary";
    public const string Copilot = "copilot.agent";
}

public interface IPromptRegistry
{
    /// <exception cref="InvalidOperationException">No hay versión activa para la clave.</exception>
    Task<PromptDefinition> GetActiveAsync(string key, CancellationToken ct);

    Task<PromptDefinition> GetVersionAsync(string key, int version, CancellationToken ct);
}

/// <summary>Validación de JSON contra JSON Schema (defensa en profundidad sobre structured outputs).</summary>
public interface IJsonSchemaValidator
{
    IReadOnlyList<string> Validate(string json, string schema);
}
