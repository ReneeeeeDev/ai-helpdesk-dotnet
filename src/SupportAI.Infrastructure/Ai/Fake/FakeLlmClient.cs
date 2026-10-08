using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Common.Text;
using SupportAI.Domain.Ai;

namespace SupportAI.Infrastructure.Ai.Fake;

/// <summary>
/// Proveedor determinista para desarrollo sin API key y para todos los tests. Interpreta los mismos bloques XML
/// que recibe Claude (&lt;categories&gt;, &lt;ticket&gt;, &lt;knowledge_base&gt;...) y produce salidas que cumplen los
/// esquemas, de modo que todo el flujo (validación, auto-aplicación, citas, streaming, tool use) se ejercita igual.
/// </summary>
internal sealed partial class FakeLlmClient(IOptions<LlmOptions> options) : ILlmClient
{
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["acceso"] = ["login", "sesion", "contrasena", "2fa", "password", "iniciar", "bloquead", "acceso", "entrar", "codigo"],
        ["facturacion"] = ["factura", "cobr", "cargo", "reembolso", "tarjeta", "pago", "invoice", "billing", "refund", "suscripcion"],
        ["rendimiento"] = ["lento", "tarda", "lentitud", "performance", "slow", "carga", "segundos"],
        ["integraciones"] = ["webhook", "api", "integracion", "401", "token", "endpoint", "signature", "firma"],
        ["cuenta"] = ["cuenta", "perfil", "usuario", "baja", "account", "email"],
        ["bug"] = ["bug", "fallo", "crash", "no funciona", "excepcion"],
        ["consulta-general"] = ["como", "pregunta", "duda", "how", "exporto", "exportar", "se puede"],
    };

    public string ProviderName => "fake";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        await Task.Delay(options.Value.FakeLatency, ct);
        var user = request.Messages.Last(m => m.Role == LlmRole.User).Content;

        // En un reintento de reparación, el turno previo del asistente está en el historial: se reconstruye la tarea original.
        var original = request.Messages.First(m => m.Role == LlmRole.User).Content;
        var text = request.Operation switch
        {
            "triage" => Triage(original),
            "reply" => Reply(original),
            "review" => Review(original),
            _ when request.JsonSchema is not null => "{}",
            _ => Summarize(user),
        };

        return new LlmResponse(text, LlmStopReasons.EndTurn, EstimateUsage(request, text), $"fake/{request.Model}", options.Value.FakeLatency);
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = Summarize(request.Messages.Last(m => m.Role == LlmRole.User).Content);
        foreach (var word in Regex.Split(text, @"(?<=\s)"))
        {
            await Task.Delay(15, ct);
            yield return new LlmStreamChunk(word, null);
        }

        yield return new LlmStreamChunk(null, new LlmResponse(text, LlmStopReasons.EndTurn, EstimateUsage(request, text), $"fake/{request.Model}", TimeSpan.Zero));
    }

    public async IAsyncEnumerable<LlmAgentEvent> RunAgentAsync(
        LlmRequest request, IReadOnlyList<LlmToolDefinition> tools, LlmToolExecutor executor, int maxIterations, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(options.Value.FakeLatency, ct);
        var prompt = request.Messages.Last(m => m.Role == LlmRole.User).Content;
        var question = Decode(Block(prompt, "question") ?? prompt);
        var normalized = Normalize(question);

        // Solo "#1042" o "ticket 1042" se interpretan como número de ticket (no códigos como "error 401").
        var number = Regex.Match(question, @"(?:#|ticket\s+#?)(\d{3,})", RegexOptions.IgnoreCase);
        var (tool, input) = number.Success && (normalized.Contains("sla", StringComparison.Ordinal) || normalized.Contains("vence", StringComparison.Ordinal))
            ? ("get_sla_status", (object)new { number = long.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) })
            : number.Success
                ? ("get_ticket", new { number = long.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) })
                : normalized.Contains("ticket", StringComparison.Ordinal) || normalized.Contains("urgente", StringComparison.Ordinal) || normalized.Contains("pendiente", StringComparison.Ordinal)
                    ? ("search_tickets", new { query = string.Empty, status = normalized.Contains("pendiente", StringComparison.Ordinal) ? "pending" : "any" })
                    : ("search_knowledge_base", (object)new { query = question });

        var inputJson = JsonSerializer.SerializeToElement(input);
        yield return new LlmAgentEvent("tool_call", inputJson.GetRawText(), tool);
        var result = await executor(tool, inputJson, ct);

        var answer = result.IsError
            ? $"No pude obtener la información: {result.Content}"
            : $"**(Proveedor de IA simulado)** Consulté `{tool}` y esto es lo que encontré:\n\n```json\n{Truncate(result.Content, 1_500)}\n```\n\nConfigura `Llm:Provider=Claude` para obtener respuestas redactadas por Claude.";
        yield return new LlmAgentEvent("text", answer);
        yield return new LlmAgentEvent("done", Usage: EstimateUsage(request, answer), Iterations: 2);
    }

    // ───────────────────────────── Triage ─────────────────────────────

    private static string Triage(string prompt)
    {
        var categories = Lines(Block(prompt, "categories")).Select(l => l.TrimStart('-', ' ')).Where(l => l.Length > 0).ToList();
        var teams = Lines(Block(prompt, "teams"))
            .Select(l => l.TrimStart('-', ' ').Split(':', 2))
            .Where(p => p.Length == 2)
            .Select(p => (Name: p[0].Trim(), Categories: p[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .ToList();
        var subject = Decode(Block(prompt, "subject") ?? string.Empty);
        var description = Decode(Block(prompt, "description") ?? string.Empty);
        var text = Normalize($"{subject} {description}");

        var scored = categories
            .Select(c => (Category: c, Hits: (Synonyms.TryGetValue(c, out var words) ? words : [c]).Where(w => text.Contains(w, StringComparison.Ordinal)).ToList()))
            .OrderByDescending(s => s.Hits.Count)
            .ToList();
        var best = scored.FirstOrDefault(s => s.Hits.Count > 0);
        var category = best.Category ?? (categories.Contains("consulta-general") ? "consulta-general" : categories.FirstOrDefault() ?? "general");
        var hits = best.Hits ?? [];

        var priority = ContainsAny(text, "no puedo", "caido", "down", "urgente", "bloquead", "parad", " 500", "perdida", "seguridad", "security")
            ? "urgent"
            : ContainsAny(text, "dos veces", "401", "no funciona", "error", "cobr")
                ? "high"
                : ContainsAny(text, "como ", "how ", "se puede", "pregunta", "duda") ? "low" : "normal";

        var sentiment = ContainsAny(text, "inaceptable", "terrible", "harto", "furioso", "!!!")
            ? "very_negative"
            : ContainsAny(text, "no puedo", "error", "problema", "urgente", "molest", "fail", "since yesterday")
                ? "negative"
                : ContainsAny(text, "gracias", "excelente", "thanks") ? "positive" : "neutral";

        var english = Regex.Matches(text, @"\b(the|and|our|your|since|with|is|we)\b").Count;
        var spanish = Regex.Matches(text, @"\b(el|la|los|las|de|que|con|desde|mi|por)\b").Count;
        var injection = ContainsAny(text, "ignore previous instructions", "ignora las instrucciones", "ignora tus instrucciones");

        var output = new
        {
            category,
            priority,
            sentiment,
            language = english > spanish ? "en" : "es",
            summary = Truncate(FirstSentence(string.IsNullOrWhiteSpace(description) ? subject : $"{subject}: {description}"), 270),
            suggested_team = teams.FirstOrDefault(t => t.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)).Name ?? string.Empty,
            tags = hits.Take(3).Select(h => h.Replace(' ', '-')).DefaultIfEmpty(category).ToArray(),
            contains_pii = Regex.IsMatch(prompt, @"\[(EMAIL|TELÉFONO|TARJETA|IBAN)\]"),
            requires_human = injection,
            confidence = injection ? 0.6m : hits.Count >= 2 ? 0.92m : hits.Count == 1 ? 0.78m : 0.55m,
            reasoning = hits.Count > 0
                ? $"Coincidencias con '{category}': {string.Join(", ", hits)}. Prioridad '{priority}' por el impacto descrito."
                : "Sin señales claras de categoría; se usa la más general con confianza baja.",
        };
        return JsonSerializer.Serialize(output);
    }

    // ───────────────────────────── Respuesta sugerida ─────────────────────────────

    private static string Reply(string prompt)
    {
        var agent = Regex.Match(prompt, @"Agente que firma:\s*(.+)").Groups[1].Value.Trim();
        var articles = Regex.Matches(prompt, "<article id=\"([^\"]+)\" title=\"([^\"]*)\">\\s*(.*?)</article>", RegexOptions.Singleline)
            .Select(m => (Id: m.Groups[1].Value, Title: Decode(m.Groups[2].Value), Body: Decode(m.Groups[3].Value)))
            .ToList();

        var sb = new StringBuilder("Hola,\n\nGracias por contactarnos. ");
        if (articles.Count > 0)
        {
            var first = articles[0];
            sb.Append(CultureInfo.InvariantCulture, $"Según nuestra guía «{first.Title}»:\n\n");
            foreach (var line in first.Body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(4))
            {
                sb.Append(line).Append('\n');
            }

            sb.Append("\nSi después de seguir estos pasos el problema continúa, respóndenos y lo revisamos contigo.");
        }
        else
        {
            sb.Append("Estamos revisando tu caso. ¿Podrías indicarnos desde cuándo ocurre y, si es posible, adjuntar una captura del error?");
        }

        sb.Append(CultureInfo.InvariantCulture, $"\n\nUn saludo,\n{(string.IsNullOrWhiteSpace(agent) ? "El equipo de soporte" : agent)}");

        return JsonSerializer.Serialize(new
        {
            reply_markdown = sb.ToString(),
            cited_article_ids = articles.Take(1).Select(a => a.Id).ToArray(),
            confidence = articles.Count > 0 ? 0.8m : 0.4m,
            should_escalate = articles.Count == 0,
            follow_up_questions = articles.Count > 0 ? Array.Empty<string>() : ["¿Desde cuándo ocurre el problema?"],
        });
    }

    // ───────────────────────────── Revisión de MR ─────────────────────────────

    private static string Review(string prompt)
    {
        var files = Regex.Matches(prompt, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
        var findings = files
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && !f.Contains("Tests", StringComparison.Ordinal))
            .Take(1)
            .Select(f => new
            {
                file = f,
                line = 1,
                severity = "nit",
                category = "test",
                title = "Verificar cobertura de tests",
                explanation = "Revisión simulada: confirma que el cambio en este archivo tiene tests que lo ejercitan.",
                suggestion = "Agrega o actualiza un test unitario o de integración para este comportamiento.",
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            summary = $"(Revisión simulada) El diff modifica {files.Count} archivo(s). Configura Claude para una revisión real.",
            risk = "low",
            findings,
            tests_recommended = Array.Empty<string>(),
        });
    }

    // ───────────────────────────── Resumen ─────────────────────────────

    private static string Summarize(string prompt)
    {
        var messages = Regex.Matches(prompt, "<message author=\"([^\"]+)\"[^>]*>(.*?)</message>", RegexOptions.Singleline)
            .Select(m => (Author: m.Groups[1].Value, Body: Decode(m.Groups[2].Value).Trim()))
            .ToList();
        var status = Regex.Match(prompt, @"Estado del ticket:\s*(\w+)").Groups[1].Value;

        if (messages.Count == 0)
        {
            return "**Problema:** no hay mensajes en el hilo.";
        }

        var agentReplies = messages.Count(m => m.Author == "agente");
        var lastAuthor = messages[^1].Author;
        return $"""
            **Problema:** {Truncate(FirstSentence(messages[0].Body), 220)}

            **Lo que ya se hizo:**
            - {messages.Count} mensaje(s) en el hilo; {agentReplies} respuesta(s) del equipo.
            - Último mensaje de: {lastAuthor}.

            **Estado actual:** {(string.IsNullOrEmpty(status) ? "desconocido" : status)}; {(lastAuthor == "cliente" ? "esperando respuesta del equipo" : "esperando al cliente")}.

            **Siguiente paso sugerido:** {(lastAuthor == "cliente" ? "responder al último mensaje del cliente" : "hacer seguimiento si no hay respuesta en 48 h")}.

            _(Resumen generado por el proveedor de IA simulado.)_
            """;
    }

    // ───────────────────────────── Utilidades ─────────────────────────────

    private static LlmUsage EstimateUsage(LlmRequest request, string output) =>
        new((request.SystemPrompt.Length + request.Messages.Sum(m => m.Content.Length)) / 4, Math.Max(1, output.Length / 4));

    private static string? Block(string text, string tag)
    {
        var match = Regex.Match(text, $"<{tag}[^>]*>(.*?)</{tag}>", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static IEnumerable<string> Lines(string? block) => (block ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Decode(string text) => WebUtility.HtmlDecode(text);

    private static string Normalize(string text) => TextNormalizer.Normalize(text);

    private static bool ContainsAny(string text, params string[] values) => values.Any(v => text.Contains(v, StringComparison.Ordinal));

    private static string FirstSentence(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        var end = flat.IndexOfAny(['.', '?', '!']);
        return end > 20 ? flat[..(end + 1)] : flat;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
