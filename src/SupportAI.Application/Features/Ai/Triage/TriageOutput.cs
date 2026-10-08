using SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Ai.Triage;

/// <summary>
/// Contrato de salida del prompt <c>ticket.triage</c>. Debe coincidir con su <c>schema.json</c>
/// (lo verifica <c>PromptCatalogTests</c>). JSON en snake_case.
/// </summary>
public sealed record TriageOutput(
    string Category,
    TriagePriority Priority,
    TriageSentiment Sentiment,
    string Language,
    string Summary,
    string SuggestedTeam,
    IReadOnlyList<string> Tags,
    bool ContainsPii,
    bool RequiresHuman,
    decimal Confidence,
    string Reasoning);

public enum TriagePriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum TriageSentiment
{
    VeryNegative,
    Negative,
    Neutral,
    Positive,
}

internal static class TriageOutputExtensions
{
    public static TicketPriority ToDomain(this TriagePriority p) => p switch
    {
        TriagePriority.Low => TicketPriority.Low,
        TriagePriority.High => TicketPriority.High,
        TriagePriority.Urgent => TicketPriority.Urgent,
        _ => TicketPriority.Normal,
    };

    public static Sentiment ToDomain(this TriageSentiment s) => s switch
    {
        TriageSentiment.VeryNegative => Sentiment.VeryNegative,
        TriageSentiment.Negative => Sentiment.Negative,
        TriageSentiment.Positive => Sentiment.Positive,
        _ => Sentiment.Neutral,
    };

    /// <summary>Reglas semánticas que el JSON Schema no puede expresar.</summary>
    public static IEnumerable<string> Validate(this TriageOutput o, IReadOnlyCollection<string> categories, IReadOnlyCollection<string> teams)
    {
        if (!categories.Contains(o.Category.Trim().ToLowerInvariant()))
        {
            yield return $"category '{o.Category}' no pertenece a la taxonomía permitida: {string.Join(", ", categories)}.";
        }

        if (!string.IsNullOrWhiteSpace(o.SuggestedTeam) && !teams.Contains(o.SuggestedTeam.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            yield return $"suggested_team '{o.SuggestedTeam}' no existe. Usa uno de: {string.Join(", ", teams)} o cadena vacía.";
        }

        if (o.Confidence is < 0 or > 1)
        {
            yield return "confidence debe estar entre 0 y 1.";
        }

        if (string.IsNullOrWhiteSpace(o.Summary) || o.Summary.Length > 280)
        {
            yield return "summary es obligatorio y debe tener como máximo 280 caracteres.";
        }

        if (o.Language.Trim().Length != 2)
        {
            yield return "language debe ser un código ISO 639-1 de 2 letras.";
        }

        if (o.Tags.Count > 8 || o.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 40))
        {
            yield return "tags: máximo 8, no vacíos y de hasta 40 caracteres.";
        }
    }
}
