using System.Security.Cryptography;
using System.Text;
using SupportAI.Domain.Common;

namespace SupportAI.Domain.Ai;

/// <summary>
/// Versión inmutable de un prompt (system + plantilla de usuario + esquema JSON + parámetros del modelo).
/// Solo una versión por <see cref="Key"/> puede estar activa.
/// </summary>
public sealed class PromptTemplate : AggregateRoot
{
    private PromptTemplate()
    {
    }

    public string Key { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string SystemPrompt { get; private set; } = string.Empty;

    public string UserTemplate { get; private set; } = string.Empty;

    /// <summary>JSON Schema de la salida estructurada. Null para prompts de texto libre (streaming, copiloto).</summary>
    public string? JsonSchema { get; private set; }

    public string Model { get; private set; } = string.Empty;

    /// <summary>Esfuerzo del modelo: low | medium | high | xhigh | max.</summary>
    public string Effort { get; private set; } = "medium";

    public int MaxTokens { get; private set; }

    /// <summary>SHA-256 del contenido. Detecta si alguien editó una versión ya publicada (prohibido).</summary>
    public string ContentHash { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public static PromptTemplate Create(
        string key,
        int version,
        string description,
        string systemPrompt,
        string userTemplate,
        string? jsonSchema,
        string model,
        string effort,
        int maxTokens,
        DateTimeOffset now)
    {
        if (version < 1)
        {
            throw new DomainException("La versión del prompt debe ser ≥ 1.", "prompt_invalid_version");
        }

        if (maxTokens is < 1 or > 128_000)
        {
            throw new DomainException("MaxTokens debe estar entre 1 y 128000.", "prompt_invalid_max_tokens");
        }

        string[] efforts = ["low", "medium", "high", "xhigh", "max"];
        if (!efforts.Contains(effort))
        {
            throw new DomainException($"Effort inválido: {effort}.", "prompt_invalid_effort");
        }

        var template = new PromptTemplate
        {
            Id = NewId(),
            Key = Guard.NotNullOrWhiteSpace(key, nameof(key), 100),
            Version = version,
            Description = description.Trim(),
            SystemPrompt = Guard.NotNullOrWhiteSpace(systemPrompt, nameof(systemPrompt)),
            UserTemplate = Guard.NotNullOrWhiteSpace(userTemplate, nameof(userTemplate)),
            JsonSchema = string.IsNullOrWhiteSpace(jsonSchema) ? null : jsonSchema,
            Model = Guard.NotNullOrWhiteSpace(model, nameof(model), 100),
            Effort = effort,
            MaxTokens = maxTokens,
            CreatedAt = now,
        };
        template.ContentHash = ComputeHash(systemPrompt, userTemplate, jsonSchema, model, effort, maxTokens);
        return template;
    }

    public static string ComputeHash(string systemPrompt, string userTemplate, string? jsonSchema, string model, string effort, int maxTokens)
    {
        var payload = string.Join("\u001F", systemPrompt, userTemplate, jsonSchema ?? string.Empty, model, effort, maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        ActivatedAt = now;
    }

    public void Deactivate() => IsActive = false;
}
