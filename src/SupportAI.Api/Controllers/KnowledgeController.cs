using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportAI.Api.Auth;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Features.Knowledge;
using SupportAI.Contracts.Knowledge;

namespace SupportAI.Api.Controllers;

/// <summary>Base de conocimiento. Los clientes ven solo artículos publicados.</summary>
[Authorize]
[Route("api/v{version:apiVersion}/knowledge-articles")]
public sealed class KnowledgeController(ISender sender) : ApiControllerBase
{
    /// <summary>Lista o busca artículos (búsqueda ranqueada por relevancia si se envía <c>search</c>).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<KnowledgeArticleSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<KnowledgeArticleSummaryDto>>> List(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] bool includeUnpublished, CancellationToken ct) =>
        Ok(await sender.Send(new GetArticlesQuery(search, category, includeUnpublished), ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<KnowledgeArticleDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await sender.Send(new GetArticleQuery(id), ct));

    [HttpPost]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<KnowledgeArticleDto>> Create(UpsertArticleRequest request, CancellationToken ct)
    {
        var article = await sender.Send(new CreateArticleCommand(request), ct);
        return Created($"/api/v1/knowledge-articles/{article.Id}", article);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<KnowledgeArticleDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<KnowledgeArticleDto>> Update(Guid id, UpsertArticleRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateArticleCommand(id, request), ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await sender.Send(new DeleteArticleCommand(id), ct);
        return NoContent();
    }
}
