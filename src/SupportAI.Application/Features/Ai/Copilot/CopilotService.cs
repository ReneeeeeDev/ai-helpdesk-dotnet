using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Application.Features.Knowledge;
using SupportAI.Contracts.Ai;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Ai.Copilot;

/// <summary>
/// Copiloto del agente: el modelo decide qué herramientas usar (buscar tickets, leer un ticket, consultar la KB o el SLA)
/// para responder preguntas en lenguaje natural. Las herramientas ejecutan consultas con el filtro de tenant del
/// DbContext, así que el modelo nunca puede ver datos de otro tenant.
/// </summary>
public sealed class CopilotService(
    IApplicationDbContext db,
    ICurrentUser user,
    ILlmClient llm,
    IPromptRegistry prompts,
    TokenBudgetGuard budget,
    ILlmCallRecorder recorder,
    IKnowledgeSearch knowledge,
    IPiiRedactor redactor,
    TimeProvider clock)
{
    private const int MaxIterations = 6;

    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static IReadOnlyList<LlmToolDefinition> Tools { get; } =
    [
        new("search_tickets",
            "Busca tickets del tenant por texto (asunto o descripción) y estado. Devuelve hasta 8 resultados con número, asunto, estado, prioridad y vencimiento de SLA.",
            """{"type":"object","additionalProperties":false,"required":["query","status"],"properties":{"query":{"type":"string","description":"Texto a buscar; vacío para listar los más recientes"},"status":{"type":"string","enum":["any","new","triaged","open","pending","resolved","closed"]}}}"""),
        new("get_ticket",
            "Obtiene el detalle de un ticket por su número (#1042 → 1042): descripción, estado, prioridad, categoría, resumen de IA y los últimos mensajes.",
            """{"type":"object","additionalProperties":false,"required":["number"],"properties":{"number":{"type":"integer"}}}"""),
        new("search_knowledge_base",
            "Busca artículos publicados de la base de conocimiento relevantes para una consulta. Devuelve título, categoría y un extracto.",
            """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}"""),
        new("get_sla_status",
            "Devuelve el estado de SLA de un ticket por número: vencimientos de primera respuesta y resolución, si está pausado y si está incumplido.",
            """{"type":"object","additionalProperties":false,"required":["number"],"properties":{"number":{"type":"integer"}}}"""),
    ];

    public async IAsyncEnumerable<CopilotStreamEvent> AskAsync(CopilotRequest question, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!user.IsStaff)
        {
            throw new ForbiddenException();
        }

        if (string.IsNullOrWhiteSpace(question.Question) || question.Question.Length > 2_000)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["question"] = ["La pregunta es obligatoria (máx. 2000 caracteres)."] });
        }

        await budget.EnsureWithinBudgetAsync(ct);
        var prompt = await prompts.GetActiveAsync(PromptKeys.Copilot, ct);

        var contextTicket = question.TicketId is { } id
            ? await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => (long?)t.Number).FirstOrDefaultAsync(ct)
            : null;

        var request = new LlmRequest
        {
            Operation = "copilot",
            Model = prompt.Model,
            Effort = prompt.Effort,
            MaxTokens = prompt.MaxTokens,
            SystemPrompt = prompt.SystemPrompt,
            Messages =
            [
                new LlmMessage(LlmRole.User, PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
                {
                    ["now"] = clock.GetUtcNow().ToString("u", CultureInfo.InvariantCulture),
                    ["context_ticket"] = contextTicket is { } n ? $"#{n}" : "ninguno",
                    ["question"] = PromptRenderer.Untrusted(question.Question),
                })),
            ],
        };

        var context = new LlmCallContext("copilot", question.TicketId, prompt.Key, prompt.Version);
        var start = Stopwatch.GetTimestamp();
        var enumerator = llm.RunAgentAsync(request, Tools, ExecuteToolAsync, MaxIterations, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                LlmAgentEvent agentEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    agentEvent = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await recorder.RecordFailureAsync(context, prompt.Model, ex, Stopwatch.GetElapsedTime(start), 1, CancellationToken.None);
                    throw;
                }

                switch (agentEvent.Type)
                {
                    case "tool_call":
                        yield return new CopilotStreamEvent("tool", agentEvent.Text, agentEvent.ToolName);
                        break;
                    case "text":
                        yield return new CopilotStreamEvent("text", agentEvent.Text);
                        break;
                    case "done":
                        var usage = agentEvent.Usage ?? LlmUsage.Empty;
                        await recorder.RecordSuccessAsync(context,
                            new LlmResponse(string.Empty, LlmStopReasons.EndTurn, usage, prompt.Model, Stopwatch.GetElapsedTime(start)),
                            agentEvent.Iterations, CancellationToken.None);
                        yield return new CopilotStreamEvent("done", null);
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>Ejecuta una herramienta pedida por el modelo. Los errores se devuelven como tool_result con is_error.</summary>
    internal async Task<LlmToolResult> ExecuteToolAsync(string toolName, JsonElement input, CancellationToken ct)
    {
        try
        {
            object result = toolName switch
            {
                "search_tickets" => await SearchTicketsAsync(input.GetProperty("query").GetString() ?? string.Empty, input.GetProperty("status").GetString() ?? "any", ct),
                "get_ticket" => await GetTicketAsync(input.GetProperty("number").GetInt64(), ct),
                "search_knowledge_base" => await SearchKnowledgeAsync(input.GetProperty("query").GetString() ?? string.Empty, ct),
                "get_sla_status" => await GetSlaAsync(input.GetProperty("number").GetInt64(), ct),
                _ => throw new InvalidOperationException($"Herramienta desconocida: {toolName}"),
            };
            return new LlmToolResult(JsonSerializer.Serialize(result, ToolJson));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or NotFoundException)
        {
            return new LlmToolResult($"Error: {ex.Message}", IsError: true);
        }
    }

    private async Task<object> SearchTicketsAsync(string query, string status, CancellationToken ct)
    {
        var tickets = db.Tickets.AsNoTracking();
        if (Enum.TryParse<TicketStatus>(status, ignoreCase: true, out var s))
        {
            tickets = tickets.Where(t => t.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            tickets = tickets.Where(t => t.Subject.Contains(q) || t.Description.Contains(q) || (t.Category != null && t.Category.Contains(q)));
        }

        var rows = await tickets.OrderByDescending(t => t.Number).Take(8)
            .Select(t => new { t.Number, t.Subject, t.Status, t.Priority, t.Category, t.ResolutionDueAt, Breached = t.FirstResponseBreached || t.ResolutionBreached })
            .ToListAsync(ct);
        return rows.Select(r => new
        {
            number = r.Number,
            subject = Safe(r.Subject),
            status = r.Status.ToString(),
            priority = r.Priority.ToString(),
            category = r.Category,
            resolution_due_at = r.ResolutionDueAt,
            sla_breached = r.Breached,
        });
    }

    private async Task<object> GetTicketAsync(long number, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Include(t => t.Messages.OrderByDescending(m => m.CreatedAt).Take(5))
            .FirstOrDefaultAsync(t => t.Number == number, ct)
            ?? throw new NotFoundException("Ticket", $"#{number}");

        return new
        {
            number = ticket.Number,
            subject = Safe(ticket.Subject),
            description = Safe(PromptRenderer.Truncate(ticket.Description, 3_000)),
            status = ticket.Status.ToString(),
            priority = ticket.Priority.ToString(),
            category = ticket.Category,
            ai_summary = ticket.AiSummary,
            needs_human_review = ticket.NeedsHumanReview,
            created_at = ticket.CreatedAt,
            last_messages = ticket.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                author = m.AuthorType.ToString(),
                @internal = m.IsInternal,
                at = m.CreatedAt,
                body = Safe(PromptRenderer.Truncate(m.Body, 1_500)),
            }),
        };
    }

    private async Task<object> SearchKnowledgeAsync(string query, CancellationToken ct)
    {
        var hits = await knowledge.SearchAsync(query, category: null, top: 3, ct);
        return hits.Select(h => new { id = h.Id, title = h.Title, category = h.Category, excerpt = PromptRenderer.Truncate(h.Body, 800) });
    }

    private async Task<object> GetSlaAsync(long number, CancellationToken ct)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Number == number, ct)
            ?? throw new NotFoundException("Ticket", $"#{number}");
        var now = clock.GetUtcNow();
        return new
        {
            number = t.Number,
            priority = t.Priority.ToString(),
            status = t.Status.ToString(),
            first_response_due_at = t.FirstResponseDueAt,
            first_responded_at = t.FirstRespondedAt,
            resolution_due_at = t.ResolutionDueAt,
            paused_since = t.SlaPausedAt,
            first_response_breached = t.FirstResponseBreached,
            resolution_breached = t.ResolutionBreached,
            minutes_to_resolution_due = t.ResolutionDueAt is { } due && t.SlaPausedAt is null ? (int)(due - now).TotalMinutes : (int?)null,
        };
    }

    /// <summary>Los resultados de herramientas también son contenido de clientes: se redacta la PII.</summary>
    private string Safe(string text) => redactor.Redact(text).Text;
}
