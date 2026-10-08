using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Ai.Summary;

/// <summary>
/// Resumen del hilo de un ticket en streaming (texto libre, sin esquema). La API lo expone como Server-Sent Events.
/// No pasa por ISender porque devuelve un IAsyncEnumerable; aplica la autorización explícitamente.
/// </summary>
public sealed class TicketSummaryService(
    IApplicationDbContext db,
    ICurrentUser user,
    ILlmClient llm,
    IPromptRegistry prompts,
    TokenBudgetGuard budget,
    ILlmCallRecorder recorder,
    IPiiRedactor redactor)
{
    public async IAsyncEnumerable<string> StreamAsync(Guid ticketId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!user.IsStaff)
        {
            throw new ForbiddenException();
        }

        var ticket = await db.Tickets.AsNoTracking()
            .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new NotFoundException("Ticket", ticketId);

        await budget.EnsureWithinBudgetAsync(ct);
        var prompt = await prompts.GetActiveAsync(PromptKeys.Summary, ct);

        var thread = new StringBuilder();
        thread.Append("<message author=\"cliente\">").Append(Clean($"{ticket.Subject}\n{ticket.Description}")).Append("</message>\n");
        foreach (var m in ticket.Messages)
        {
            var author = m.AuthorType == MessageAuthorType.Customer ? "cliente" : m.IsInternal ? "nota-interna" : "agente";
            thread.Append($"<message author=\"{author}\" at=\"{m.CreatedAt:u}\">").Append(Clean(m.Body)).Append("</message>\n");
        }

        var request = new LlmRequest
        {
            Operation = "summary",
            Model = prompt.Model,
            Effort = prompt.Effort,
            MaxTokens = prompt.MaxTokens,
            SystemPrompt = prompt.SystemPrompt,
            Messages =
            [
                new LlmMessage(LlmRole.User, PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
                {
                    ["status"] = ticket.Status.ToString(),
                    ["priority"] = ticket.Priority.ToString(),
                    ["thread"] = PromptRenderer.Truncate(thread.ToString(), 16_000),
                })),
            ],
        };

        var context = new LlmCallContext("summary", ticket.Id, prompt.Key, prompt.Version);
        var start = Stopwatch.GetTimestamp();
        var enumerator = llm.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                LlmStreamChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    chunk = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await recorder.RecordFailureAsync(context, prompt.Model, ex, Stopwatch.GetElapsedTime(start), 1, CancellationToken.None);
                    throw;
                }

                if (chunk.TextDelta is { Length: > 0 } delta)
                {
                    yield return delta;
                }

                if (chunk.Final is { } final)
                {
                    await recorder.RecordSuccessAsync(context, final, 1, CancellationToken.None);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private string Clean(string text) => PromptRenderer.Untrusted(redactor.Redact(text).Text);
}
