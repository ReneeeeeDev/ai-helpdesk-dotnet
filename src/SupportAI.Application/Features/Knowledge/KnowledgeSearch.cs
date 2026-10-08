using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Data;

namespace SupportAI.Application.Features.Knowledge;

public sealed record KnowledgeHit(Guid Id, string Title, string? Category, string Body, double Score);

/// <summary>Recuperación de artículos relevantes para un texto (usada por respuestas sugeridas, copiloto y búsqueda).</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string text, string? category, int top, CancellationToken ct);
}

/// <summary>
/// Búsqueda léxica con ranking: SQL hace el prefiltro (LIKE por palabra clave vía OPENJSON) y la app puntúa
/// título (×3), tags/categoría (×2) y cuerpo (×1). LocalDB no tiene Full-Text Search; en producción se podría
/// reemplazar por FTS o por búsqueda vectorial sin cambiar esta interfaz.
/// </summary>
internal sealed class KnowledgeSearch(IApplicationDbContext db) : IKnowledgeSearch
{
    private const int MaxKeywords = 12;
    private const string AccentInsensitiveCollation = "Latin1_General_CI_AI";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // ES
        "para", "como", "pero", "porque", "desde", "hasta", "este", "esta", "esto", "estos", "estas", "tengo", "tiene", "hace",
        "puedo", "puede", "sobre", "cuando", "donde", "todo", "todos", "mucho", "muy", "ayer", "hoy", "necesito", "quiero",
        "hola", "gracias", "favor", "algun", "alguna", "otra", "otro", "sigue", "solo", "ahora", "entre", "nuestro", "nuestra",
        "aparece", "aparecen", "tambien", "despues", "antes", "error", "problema", "cuenta",
        // EN
        "the", "and", "for", "with", "from", "that", "this", "have", "your", "since", "when", "what", "there", "their", "they",
        "into", "our", "any", "known", "issue", "started", "please", "thanks", "hello",
    };

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string text, string? category, int top, CancellationToken ct)
    {
        var keywords = ExtractKeywords(text).Take(MaxKeywords).ToList();
        var normalizedCategory = category?.Trim().ToLowerInvariant();
        if (keywords.Count == 0 && normalizedCategory is null)
        {
            return [];
        }

        // Prefiltro en SQL: artículos publicados que contengan alguna palabra clave o sean de la categoría.
        // COLLATE *_CI_AI: insensible a mayúsculas y tildes ("facturacion" encuentra "Facturación").
        var candidates = await db.KnowledgeArticles.AsNoTracking()
            .Where(a => a.IsPublished)
            .Where(a => a.Category == normalizedCategory
                || keywords.Any(k => EF.Functions.Collate(a.Title, AccentInsensitiveCollation).Contains(k)
                    || EF.Functions.Collate(a.Body, AccentInsensitiveCollation).Contains(k)))
            .Select(a => new { a.Id, a.Title, a.Category, a.Body, a.Tags })
            .Take(100)
            .ToListAsync(ct);

        return candidates
            .Select(a =>
            {
                var title = Normalize(a.Title);
                var body = Normalize(a.Body);
                var tags = a.Tags.Select(Normalize).ToHashSet(StringComparer.Ordinal);
                double score = 0;
                foreach (var k in keywords)
                {
                    if (title.Contains(k, StringComparison.Ordinal))
                    {
                        score += 3;
                    }

                    if (tags.Any(t => t.Contains(k, StringComparison.Ordinal)))
                    {
                        score += 2;
                    }

                    if (body.Contains(k, StringComparison.Ordinal))
                    {
                        score += 1;
                    }
                }

                if (normalizedCategory is not null && a.Category == normalizedCategory)
                {
                    score += 2;
                }

                return new KnowledgeHit(a.Id, a.Title, a.Category, a.Body, score);
            })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    internal static IEnumerable<string> ExtractKeywords(string text) =>
        Normalize(text)
            .Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '¿', '?', '¡', '!', '(', ')', '"', '\'', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w) && !w.All(char.IsDigit))
            .GroupBy(w => w, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Length)
            .Select(g => g.Key);

    /// <summary>Minúsculas y sin tildes, para comparar "Facturación" con "facturacion".</summary>
    internal static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
