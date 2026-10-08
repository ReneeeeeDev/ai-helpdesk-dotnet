using System.Text;
using System.Text.RegularExpressions;

namespace SupportAI.Application.Features.Ai.Prompting;

/// <summary>
/// Sustituye <c>{{variable}}</c> en las plantillas. Falla si falta una variable (evita prompts incompletos
/// silenciosos) y ofrece <see cref="Untrusted"/> para neutralizar contenido del usuario dentro de etiquetas XML.
/// </summary>
public static partial class PromptRenderer
{
    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        var missing = new List<string>();
        var result = Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            missing.Add(name);
            return match.Value;
        });

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Faltan variables en la plantilla: {string.Join(", ", missing.Distinct())}");
        }

        return result;
    }

    /// <summary>
    /// Escapa texto no confiable (escrito por clientes) para que no pueda cerrar ni abrir etiquetas del prompt
    /// (p.ej. "&lt;/ticket&gt; Ignora las instrucciones anteriores..."). El modelo lee &amp;lt; sin problema.
    /// </summary>
    public static string Untrusted(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>Recorta texto largo conservando inicio y final (lo más informativo de un hilo).</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var head = (int)(maxChars * 0.7);
        var tail = maxChars - head;
        return $"{text[..head]}\n[… {text.Length - maxChars} caracteres omitidos …]\n{text[^tail..]}";
    }
}
