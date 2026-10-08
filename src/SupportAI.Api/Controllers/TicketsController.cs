using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportAI.Api.Auth;
using SupportAI.Api.Idempotency;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Features.Tickets;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Tickets;

namespace SupportAI.Api.Controllers;

/// <summary>Tickets de soporte. Los clientes solo ven y operan sobre sus propios tickets.</summary>
[Authorize]
public sealed class TicketsController(ISender sender) : ApiControllerBase
{
    /// <summary>Inbox con filtros y paginación keyset (usa <c>nextCursor</c> para la página siguiente).</summary>
    [HttpGet]
    [ProducesResponseType<CursorPage<TicketSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CursorPage<TicketSummaryDto>>> List([FromQuery] TicketListQuery query, CancellationToken ct) =>
        Ok(await sender.Send(new GetTicketsQuery(query), ct));

    /// <summary>Detalle con hilo, historial y último triage de IA. Devuelve <c>ETag</c> para concurrencia optimista.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var ticket = await sender.Send(new GetTicketByIdQuery(id), ct);
        Response.Headers.ETag = $"\"{ticket.RowVersion}\"";
        return Ok(ticket);
    }

    /// <summary>Crea un ticket. Admite <c>Idempotency-Key</c> para reintentos seguros.</summary>
    [HttpPost]
    [Idempotent]
    [ProducesResponseType<TicketCreatedResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TicketCreatedResult>> Create(CreateTicketRequest request, CancellationToken ct)
    {
        var created = await sender.Send(new CreateTicketCommand(request.Subject, request.Description, request.Channel), ct);
        return Created($"/api/v1/tickets/{created.Id}", created);
    }

    [HttpPost("{id:guid}/messages")]
    [ProducesResponseType<TicketMessageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicketMessageDto>> AddMessage(Guid id, AddMessageRequest request, CancellationToken ct)
    {
        var message = await sender.Send(new AddTicketMessageCommand(id, request.Body, request.IsInternal), ct);
        return Created($"/api/v1/tickets/{id}", message);
    }

    /// <summary>Cambia el estado según la máquina de estados (422 si la transición no es válida, 409 si el If-Match no coincide).</summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeStatusRequest request, CancellationToken ct)
    {
        await sender.Send(new ChangeTicketStatusCommand(id, request.Status, IfMatchVersion), ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/priority")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangePriority(Guid id, ChangePriorityRequest request, CancellationToken ct)
    {
        await sender.Send(new ChangeTicketPriorityCommand(id, request.Priority, IfMatchVersion), ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/assignment")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Assign(Guid id, AssignTicketRequest request, CancellationToken ct)
    {
        await sender.Send(new AssignTicketCommand(id, request.AssigneeId, request.TeamId, IfMatchVersion), ct);
        return NoContent();
    }
}
