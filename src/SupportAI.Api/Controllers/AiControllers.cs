using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportAI.Api.Auth;
using SupportAI.Api.Extensions;
using SupportAI.Api.Streaming;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Features.Ai.Admin;
using SupportAI.Application.Features.Ai.Copilot;
using SupportAI.Application.Features.Ai.Replies;
using SupportAI.Application.Features.Ai.Summary;
using SupportAI.Application.Features.Ai.Triage;
using SupportAI.Contracts.Ai;

namespace SupportAI.Api.Controllers;

/// <summary>Funciones de IA sobre un ticket: triage, respuestas sugeridas y resumen en streaming.</summary>
[Authorize(Policy = Policies.Staff)]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/ai")]
public sealed class TicketAiController(ISender sender) : ApiControllerBase
{
    /// <summary>Historial de resultados de triage (el más reciente primero).</summary>
    [HttpGet("triage")]
    [ProducesResponseType<IReadOnlyList<TriageResultDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TriageResultDto>>> TriageHistory(Guid ticketId, CancellationToken ct) =>
        Ok(await sender.Send(new GetTriageHistoryQuery(ticketId), ct));

    /// <summary>Vuelve a ejecutar el triage con la versión activa del prompt.</summary>
    [HttpPost("triage")]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    [ProducesResponseType<TriageResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TriageResultDto>> Retriage(Guid ticketId, CancellationToken ct) =>
        Ok(await sender.Send(new RetriageTicketCommand(ticketId), ct));

    [HttpPost("triage/{resultId:guid}/accept")]
    [ProducesResponseType<TriageResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TriageResultDto>> Accept(Guid ticketId, Guid resultId, CancellationToken ct) =>
        Ok(await sender.Send(new AcceptTriageCommand(ticketId, resultId), ct));

    [HttpPost("triage/{resultId:guid}/override")]
    [ProducesResponseType<TriageResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TriageResultDto>> Override(Guid ticketId, Guid resultId, OverrideTriageRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new OverrideTriageCommand(ticketId, resultId, request.Category, request.Priority, request.TeamId, request.Notes), ct));

    /// <summary>Genera un borrador de respuesta con citas a la base de conocimiento.</summary>
    [HttpPost("reply-suggestions")]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    [ProducesResponseType<SuggestedReplyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuggestedReplyDto>> SuggestReply(Guid ticketId, CancellationToken ct) =>
        Ok(await sender.Send(new SuggestReplyCommand(ticketId), ct));

    [HttpPost("reply-suggestions/{replyId:guid}/used")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkReplyUsed(Guid ticketId, Guid replyId, CancellationToken ct)
    {
        await sender.Send(new ResolveSuggestedReplyCommand(ticketId, replyId, Used: true), ct);
        return NoContent();
    }

    [HttpPost("reply-suggestions/{replyId:guid}/discard")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DiscardReply(Guid ticketId, Guid replyId, CancellationToken ct)
    {
        await sender.Send(new ResolveSuggestedReplyCommand(ticketId, replyId, Used: false), ct);
        return NoContent();
    }

    /// <summary>Resumen del hilo en streaming (Server-Sent Events: eventos <c>delta</c>, <c>end</c> o <c>error</c>).</summary>
    [HttpGet("summary/stream")]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    [Produces("text/event-stream")]
    public Task StreamSummary(Guid ticketId, [FromServices] TicketSummaryService summaries, [FromServices] ServerSentEvents sse, CancellationToken ct) =>
        sse.WriteAsync(HttpContext, AsDeltas(summaries.StreamAsync(ticketId, ct)), _ => "delta", ct);

    private static async IAsyncEnumerable<SummaryDelta> AsDeltas(IAsyncEnumerable<string> source)
    {
        await foreach (var text in source)
        {
            yield return new SummaryDelta(text);
        }
    }

    public sealed record SummaryDelta(string Text);
}

/// <summary>Copiloto conversacional para agentes, con tool use sobre tickets, SLA y base de conocimiento.</summary>
[Authorize(Policy = Policies.Staff)]
[Route("api/v{version:apiVersion}/ai/copilot")]
public sealed class CopilotController : ApiControllerBase
{
    /// <summary>Respuesta en streaming SSE: eventos <c>tool</c> (herramienta usada), <c>text</c>, <c>done</c>.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    [Produces("text/event-stream")]
    public Task Ask(CopilotRequest request, [FromServices] CopilotService copilot, [FromServices] ServerSentEvents sse, CancellationToken ct) =>
        sse.WriteAsync(HttpContext, copilot.AskAsync(request, ct), e => e.Type, ct);
}

/// <summary>Administración de IA: uso, costos, precisión por versión de prompt y activación de versiones.</summary>
[Authorize(Policy = Policies.Admin)]
[Route("api/v{version:apiVersion}/ai")]
public sealed class AiAdminController(ISender sender) : ApiControllerBase
{
    [HttpGet("usage")]
    [ProducesResponseType<AiUsageReportDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AiUsageReportDto>> Usage([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await sender.Send(new GetAiUsageQuery(from, to), ct));

    [HttpGet("prompts")]
    [ProducesResponseType<IReadOnlyList<PromptTemplateDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PromptTemplateDto>>> Prompts(CancellationToken ct) =>
        Ok(await sender.Send(new GetPromptTemplatesQuery(), ct));

    [HttpGet("prompts/{id:guid}")]
    [ProducesResponseType<PromptTemplateDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromptTemplateDetailDto>> Prompt(Guid id, CancellationToken ct) =>
        Ok(await sender.Send(new GetPromptTemplateQuery(id), ct));

    /// <summary>Activa una versión de un prompt (revertir = activar la anterior). Sin redeploy.</summary>
    [HttpPost("prompts/{key}/versions/{promptVersion:int}/activate")]
    [ProducesResponseType<PromptTemplateDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromptTemplateDto>> Activate(string key, int promptVersion, CancellationToken ct) =>
        Ok(await sender.Send(new ActivatePromptVersionCommand(key, promptVersion), ct));
}
