using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Ai.Triage;

/// <summary>
/// Clasifica un ticket con el LLM y decide si aplicar el resultado automáticamente
/// (confianza ≥ umbral del tenant y sin <c>requires_human</c>) o dejarlo como sugerencia para un agente.
/// </summary>
public sealed partial class TicketTriageService(
    IApplicationDbContext db,
    IPromptRegistry prompts,
    StructuredCompletionService structured,
    TokenBudgetGuard budget,
    IPiiRedactor redactor,
    TimeProvider clock,
    ILogger<TicketTriageService> logger)
{
    /// <param name="manual">true si lo pidió un agente (ignora auto-triage desactivado y resultados previos).</param>
    /// <returns>El resultado, o null si no correspondía ejecutar el triage.</returns>
    public async Task<AiTriageResult?> TriageAsync(Guid ticketId, bool manual, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.Status == TicketStatus.Closed)
        {
            return null;
        }

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ticket.TenantId, ct);
        if (!manual)
        {
            if (!tenant.AiSettings.AutoTriageEnabled)
            {
                return null;
            }

            // Idempotencia frente a reentregas del outbox: un triage automático por ticket.
            if (await db.TriageResults.AnyAsync(r => r.TicketId == ticketId, ct))
            {
                return null;
            }
        }

        try
        {
            await budget.EnsureWithinBudgetAsync(ct);
        }
        catch (QuotaExceededException) when (!manual)
        {
            ticket.FlagForHumanReview("presupuesto de IA agotado", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return null;
        }

        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.Categories }).ToListAsync(ct);
        var categories = tenant.Categories.ToList();
        var prompt = await prompts.GetActiveAsync(PromptKeys.Triage, ct);

        var subject = redactor.Redact(ticket.Subject);
        var description = redactor.Redact(ticket.Description);
        var userMessage = BuildUserMessage(prompt, categories, teams.Select(t => (t.Name, t.Categories)).ToList(),
            ticket.Channel.ToString(), ticket.CreatedAt, subject.Text, description.Text);

        var teamNames = teams.Select(t => t.Name).ToList();
        var result = await structured.CompleteAsync<TriageOutput>(
            prompt,
            userMessage,
            new LlmCallContext("triage", ticket.Id, prompt.Key, prompt.Version),
            output => output.Validate(categories, teamNames),
            ct,
            // Enum dinámico por tenant: el decoding restringido garantiza categoría y equipo válidos.
            schema => ConstrainSchema(schema, categories, teamNames));

        var now = clock.GetUtcNow();
        AiTriageResult triage;

        if (!result.IsValid || result.Value is null)
        {
            triage = AiTriageResult.CreateInvalid(ticket.TenantId, ticket.Id, prompt.Key, prompt.Version, prompt.Model,
                result.RawJson, string.Join(" | ", result.Errors), result.LlmCallId, now);
            ticket.FlagForHumanReview("la salida de IA no pasó la validación", now);
        }
        else
        {
            var o = result.Value;
            var team = teams.FirstOrDefault(t => string.Equals(t.Name, o.SuggestedTeam.Trim(), StringComparison.OrdinalIgnoreCase));
            var decision = new TriageDecision(o.Category.Trim().ToLowerInvariant(), o.Priority.ToDomain(), o.Sentiment.ToDomain(),
                o.Language.Trim().ToLowerInvariant(), team?.Id, o.Tags, o.Summary.Trim());

            triage = AiTriageResult.Create(ticket.TenantId, ticket.Id, prompt.Key, prompt.Version, prompt.Model, decision, team?.Name,
                containsPii: o.ContainsPii || subject.FoundAny || description.FoundAny,
                o.RequiresHuman, o.Confidence, o.Reasoning, result.RawJson, result.LlmCallId, now);

            var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);
            if (tenant.AiSettings.ShouldAutoApply(o.Confidence, o.RequiresHuman))
            {
                ticket.ApplyTriage(decision, policy, actorId: null, byAi: true, now);
                triage.MarkAutoApplied();
            }
            else
            {
                ticket.FlagForHumanReview(
                    o.RequiresHuman ? "el modelo pidió revisión humana" : $"confianza {o.Confidence.ToString("P0", CultureInfo.InvariantCulture)} bajo el umbral",
                    now);
            }
        }

        db.TriageResults.Add(triage);
        await db.SaveChangesAsync(ct);
        LogTriaged(logger, ticket.Id, triage.Status, triage.Confidence, prompt.Version);
        return triage;
    }

    /// <summary>Renderiza el mensaje de usuario del triage. Lo comparten el servicio y el harness de evaluación.</summary>
    internal static string BuildUserMessage(
        PromptDefinition prompt,
        IReadOnlyList<string> categories,
        IReadOnlyList<(string Name, IReadOnlyList<string> Categories)> teams,
        string channel,
        DateTimeOffset createdAt,
        string redactedSubject,
        string redactedDescription) =>
        PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
        {
            ["categories"] = string.Join('\n', categories.Select(c => $"- {c}")),
            ["teams"] = string.Join('\n', teams.Select(t => $"- {t.Name}: {string.Join(", ", t.Categories)}")),
            ["channel"] = channel,
            ["created_at"] = createdAt.ToString("u", CultureInfo.InvariantCulture),
            ["subject"] = PromptRenderer.Untrusted(redactedSubject),
            ["description"] = PromptRenderer.Untrusted(PromptRenderer.Truncate(redactedDescription, 8_000)),
        });

    internal static JsonObject ConstrainSchema(JsonObject schema, IReadOnlyList<string> categories, IReadOnlyList<string> teams)
    {
        var properties = schema["properties"]!.AsObject();
        properties["category"]!.AsObject()["enum"] = new JsonArray(categories.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        properties["suggested_team"]!.AsObject()["enum"] = new JsonArray(teams.Append(string.Empty).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        return schema;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} triado por IA: {Status}, confianza {Confidence}, prompt v{PromptVersion}")]
    private static partial void LogTriaged(ILogger logger, Guid ticketId, TriageResultStatus status, decimal confidence, int promptVersion);
}
