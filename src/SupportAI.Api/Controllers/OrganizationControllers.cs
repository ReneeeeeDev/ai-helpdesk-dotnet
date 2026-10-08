using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupportAI.Api.Auth;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Features.Organization;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Organization;

namespace SupportAI.Api.Controllers;

[Authorize(Policy = Policies.Staff)]
public sealed class TeamsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TeamDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TeamDto>>> List(CancellationToken ct) => Ok(await sender.Send(new GetTeamsQuery(), ct));

    [HttpPost]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<TeamDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamDto>> Create(UpsertTeamRequest request, CancellationToken ct)
    {
        var team = await sender.Send(new CreateTeamCommand(request), ct);
        return Created($"/api/v1/teams/{team.Id}", team);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<TeamDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TeamDto>> Update(Guid id, UpsertTeamRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateTeamCommand(id, request), ct));
}

[Authorize(Policy = Policies.Staff)]
public sealed class MembersController(ISender sender) : ApiControllerBase
{
    /// <summary>Miembros del tenant (útil para asignar tickets: <c>?role=Agent</c>).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MemberDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> List([FromQuery] UserRole? role, CancellationToken ct) =>
        Ok(await sender.Send(new GetMembersQuery(role), ct));
}

[Authorize(Policy = Policies.Staff)]
[Route("api/v{version:apiVersion}/sla-policy")]
public sealed class SlaPolicyController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<SlaPolicyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SlaPolicyDto>> Get(CancellationToken ct) => Ok(await sender.Send(new GetSlaPolicyQuery(), ct));

    [HttpPut]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<SlaPolicyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SlaPolicyDto>> Update(IReadOnlyList<SlaTargetDto> targets, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateSlaPolicyCommand(targets), ct));
}

[Authorize(Policy = Policies.Admin)]
[Route("api/v{version:apiVersion}/settings")]
public sealed class SettingsController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<TenantSettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantSettingsDto>> Get(CancellationToken ct) => Ok(await sender.Send(new GetTenantSettingsQuery(), ct));

    [HttpPut]
    [ProducesResponseType<TenantSettingsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantSettingsDto>> Update(UpdateTenantSettingsRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateTenantSettingsCommand(request), ct));
}

/// <summary>Taxonomía de categorías del tenant (para formularios de triage y filtros).</summary>
[Authorize(Policy = Policies.Staff)]
public sealed class CategoriesController(ISender sender) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<string>>> List(CancellationToken ct) => Ok(await sender.Send(new GetCategoriesQuery(), ct));
}
