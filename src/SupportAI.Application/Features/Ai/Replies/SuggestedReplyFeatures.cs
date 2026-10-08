using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Application.Features.Knowledge;
using SupportAI.Contracts.Ai;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;

namespace SupportAI.Application.Features.Ai.Replies;

/// <summary>Contrato de salida del prompt <c>ticket.reply</c> (ver su schema.json).</summary>
public sealed record ReplyOutput(
    string ReplyMarkdown,
    IReadOnlyList<string> CitedArticleIds,
    decimal Confidence,
    bool ShouldEscalate,
    IReadOnlyList<string> FollowUpQuestions);

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record SuggestReplyCommand(Guid TicketId) : ICommand<SuggestedReplyDto>;

internal sealed class SuggestReplyHandler(
    IApplicationDbContext db,
    ICurrentUser user,
    IPromptRegistry prompts,
    StructuredCompletionService structured,
    TokenBudgetGuard budget,
    IKnowledgeSearch knowledge,
    IPiiRedactor redactor,
    TimeProvider clock) : ICommandHandler<SuggestReplyCommand, SuggestedReplyDto>
{
    private const int MaxArticles = 4;

    public async Task<SuggestedReplyDto> Handle(SuggestReplyCommand command, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, ct)
            ?? throw new NotFoundException("Ticket", command.TicketId);

        await budget.EnsureWithinBudgetAsync(ct);

        var lastCustomerMessage = ticket.Messages.LastOrDefault(m => m.AuthorType == MessageAuthorType.Customer)?.Body;
        var hits = await knowledge.SearchAsync($"{ticket.Subject} {ticket.Description} {lastCustomerMessage}", ticket.Category, MaxArticles, ct);
        var agentName = await db.Members.Where(m => m.Id == user.RequiredUserId).Select(m => m.DisplayName).FirstAsync(ct);
        var prompt = await prompts.GetActiveAsync(PromptKeys.Reply, ct);

        var userMessage = PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
        {
            ["language"] = ticket.Language ?? "es",
            ["agent_name"] = agentName,
            ["category"] = ticket.Category ?? "sin clasificar",
            ["articles"] = hits.Count == 0
                ? "(no hay artículos relevantes)"
                : string.Join("\n", hits.Select(h =>
                    $"<article id=\"{h.Id}\" title=\"{PromptRenderer.Untrusted(h.Title)}\">\n{PromptRenderer.Untrusted(PromptRenderer.Truncate(h.Body, 2_000))}\n</article>")),
            ["thread"] = BuildThread(ticket),
        });

        var allowedIds = hits.Select(h => h.Id.ToString()).ToList();
        var result = await structured.CompleteAsync<ReplyOutput>(
            prompt,
            userMessage,
            new LlmCallContext("reply", ticket.Id, prompt.Key, prompt.Version),
            o => Validate(o, allowedIds),
            ct,
            schema => ConstrainCitations(schema, allowedIds));

        if (!result.IsValid || result.Value is null)
        {
            throw new AiUnavailableException("La IA no pudo generar una respuesta válida para este ticket. Inténtalo de nuevo.");
        }

        var output = result.Value;
        var cited = output.CitedArticleIds.Select(Guid.Parse).ToList();
        var reply = AiSuggestedReply.Create(ticket.TenantId, ticket.Id, prompt.Version, output.ReplyMarkdown, cited, output.FollowUpQuestions,
            output.Confidence, output.ShouldEscalate, user.RequiredUserId, result.LlmCallId, clock.GetUtcNow());
        db.SuggestedReplies.Add(reply);
        await db.SaveChangesAsync(ct);

        var titles = hits.ToDictionary(h => h.Id, h => h.Title);
        return new SuggestedReplyDto(reply.Id, reply.Body,
            cited.Where(titles.ContainsKey).Select(id => new CitationDto(id, titles[id])).ToList(),
            reply.FollowUpQuestions, reply.Confidence, reply.ShouldEscalate, reply.CreatedAt);
    }

    private string BuildThread(Ticket ticket)
    {
        var sb = new StringBuilder();
        sb.Append("<message author=\"cliente\">\n").Append(Clean($"{ticket.Subject}\n\n{ticket.Description}")).Append("\n</message>\n");
        foreach (var m in ticket.Messages)
        {
            var author = m.AuthorType switch
            {
                MessageAuthorType.Customer => "cliente",
                MessageAuthorType.Agent when m.IsInternal => "nota-interna-agente",
                MessageAuthorType.Agent => "agente",
                _ => "sistema",
            };
            sb.Append($"<message author=\"{author}\">\n").Append(Clean(m.Body)).Append("\n</message>\n");
        }

        return PromptRenderer.Truncate(sb.ToString(), 12_000);
    }

    private string Clean(string text) => PromptRenderer.Untrusted(redactor.Redact(text).Text);

    private static IEnumerable<string> Validate(ReplyOutput o, IReadOnlyCollection<string> allowedIds)
    {
        if (string.IsNullOrWhiteSpace(o.ReplyMarkdown) || o.ReplyMarkdown.Length > 6_000)
        {
            yield return "reply_markdown es obligatorio y debe tener como máximo 6000 caracteres.";
        }

        foreach (var id in o.CitedArticleIds.Where(id => !allowedIds.Contains(id)))
        {
            yield return $"cited_article_ids contiene '{id}', que no es uno de los artículos proporcionados.";
        }

        if (o.Confidence is < 0 or > 1)
        {
            yield return "confidence debe estar entre 0 y 1.";
        }

        if (o.FollowUpQuestions.Count > 3)
        {
            yield return "follow_up_questions: máximo 3.";
        }
    }

    private static JsonObject ConstrainCitations(JsonObject schema, IReadOnlyList<string> allowedIds)
    {
        if (allowedIds.Count > 0)
        {
            var items = schema["properties"]!["cited_article_ids"]!["items"]!.AsObject();
            items["enum"] = new JsonArray(allowedIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        }

        return schema;
    }
}

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record ResolveSuggestedReplyCommand(Guid TicketId, Guid ReplyId, bool Used) : ICommand;

internal sealed class ResolveSuggestedReplyHandler(IApplicationDbContext db) : ICommandHandler<ResolveSuggestedReplyCommand>
{
    public async Task<Unit> Handle(ResolveSuggestedReplyCommand command, CancellationToken ct)
    {
        var reply = await db.SuggestedReplies.FirstOrDefaultAsync(r => r.Id == command.ReplyId && r.TicketId == command.TicketId, ct)
            ?? throw new NotFoundException("Respuesta sugerida", command.ReplyId);
        if (command.Used)
        {
            reply.MarkUsed();
        }
        else
        {
            reply.Discard();
        }

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
