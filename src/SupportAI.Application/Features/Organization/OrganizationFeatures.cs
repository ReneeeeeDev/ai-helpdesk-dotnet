using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Common.Mapping;
using SupportAI.Contracts.Organization;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Teams;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Users;
using C = SupportAI.Contracts.Common;

namespace SupportAI.Application.Features.Organization;

// ───────────────────────────── Equipos ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record GetTeamsQuery : IQuery<IReadOnlyList<TeamDto>>;

internal sealed class GetTeamsHandler(IApplicationDbContext db) : IQueryHandler<GetTeamsQuery, IReadOnlyList<TeamDto>>
{
    public async Task<IReadOnlyList<TeamDto>> Handle(GetTeamsQuery query, CancellationToken ct)
    {
        var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var counts = await db.Members.AsNoTracking().Where(m => m.TeamId != null)
            .GroupBy(m => m.TeamId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return teams.Select(t => t.ToDto(counts.GetValueOrDefault(t.Id))).ToList();
    }
}

[AuthorizeRoles(UserRole.Admin)]
public sealed record CreateTeamCommand(UpsertTeamRequest Team) : ICommand<TeamDto>;

[AuthorizeRoles(UserRole.Admin)]
public sealed record UpdateTeamCommand(Guid Id, UpsertTeamRequest Team) : ICommand<TeamDto>;

internal sealed class UpsertTeamRequestValidator : AbstractValidator<UpsertTeamRequest>
{
    public UpsertTeamRequestValidator()
    {
        RuleFor(t => t.Name).NotEmpty().MaximumLength(80);
        RuleFor(t => t.Description).MaximumLength(500);
        RuleForEach(t => t.Categories).NotEmpty().MaximumLength(60);
    }
}

internal sealed class CreateTeamValidator : AbstractValidator<CreateTeamCommand>
{
    public CreateTeamValidator() => RuleFor(c => c.Team).NotNull().SetValidator(new UpsertTeamRequestValidator());
}

internal sealed class UpdateTeamValidator : AbstractValidator<UpdateTeamCommand>
{
    public UpdateTeamValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Team).NotNull().SetValidator(new UpsertTeamRequestValidator());
    }
}

internal sealed class CreateTeamHandler(IApplicationDbContext db, ITenantContext tenant) : ICommandHandler<CreateTeamCommand, TeamDto>
{
    public async Task<TeamDto> Handle(CreateTeamCommand command, CancellationToken ct)
    {
        var name = command.Team.Name.Trim();
        if (await db.Teams.AnyAsync(t => t.Name == name, ct))
        {
            throw new ConflictException($"Ya existe un equipo llamado '{name}'.", "team_name_taken");
        }

        var team = Team.Create(tenant.RequiredTenantId, name, command.Team.Description, command.Team.Categories ?? []);
        db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        return team.ToDto(0);
    }
}

internal sealed class UpdateTeamHandler(IApplicationDbContext db) : ICommandHandler<UpdateTeamCommand, TeamDto>
{
    public async Task<TeamDto> Handle(UpdateTeamCommand command, CancellationToken ct)
    {
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == command.Id, ct) ?? throw new NotFoundException("Equipo", command.Id);
        team.Update(command.Team.Name, command.Team.Description, command.Team.Categories ?? []);
        await db.SaveChangesAsync(ct);
        var members = await db.Members.CountAsync(m => m.TeamId == team.Id, ct);
        return team.ToDto(members);
    }
}

// ───────────────────────────── Miembros ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record GetMembersQuery(C.UserRole? Role) : IQuery<IReadOnlyList<MemberDto>>;

internal sealed class GetMembersHandler(IApplicationDbContext db) : IQueryHandler<GetMembersQuery, IReadOnlyList<MemberDto>>
{
    public async Task<IReadOnlyList<MemberDto>> Handle(GetMembersQuery query, CancellationToken ct)
    {
        var members = db.Members.AsNoTracking();
        if (query.Role is { } role)
        {
            var domainRole = (UserRole)(int)role;
            members = members.Where(m => m.Role == domainRole);
        }

        var list = await members.OrderBy(m => m.DisplayName).ToListAsync(ct);
        return list.Select(m => new MemberDto(m.Id, m.Email, m.DisplayName, m.Role.ToContract(), m.TeamId, m.IsActive)).ToList();
    }
}

// ───────────────────────────── SLA ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record GetSlaPolicyQuery : IQuery<SlaPolicyDto>;

internal sealed class GetSlaPolicyHandler(IApplicationDbContext db) : IQueryHandler<GetSlaPolicyQuery, SlaPolicyDto>
{
    public async Task<SlaPolicyDto> Handle(GetSlaPolicyQuery query, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct)
            ?? throw new NotFoundException("Política de SLA", "default");
        return policy.ToDto();
    }
}

[AuthorizeRoles(UserRole.Admin)]
public sealed record UpdateSlaPolicyCommand(IReadOnlyList<SlaTargetDto> Targets) : ICommand<SlaPolicyDto>;

internal sealed class UpdateSlaPolicyValidator : AbstractValidator<UpdateSlaPolicyCommand>
{
    public UpdateSlaPolicyValidator()
    {
        RuleFor(c => c.Targets).NotEmpty();
        RuleForEach(c => c.Targets).ChildRules(t =>
        {
            t.RuleFor(x => x.Priority).IsInEnum();
            t.RuleFor(x => x.FirstResponseMinutes).GreaterThan(0);
            t.RuleFor(x => x.ResolutionMinutes).GreaterThanOrEqualTo(x => x.FirstResponseMinutes);
        });
    }
}

internal sealed class UpdateSlaPolicyHandler(IApplicationDbContext db) : ICommandHandler<UpdateSlaPolicyCommand, SlaPolicyDto>
{
    public async Task<SlaPolicyDto> Handle(UpdateSlaPolicyCommand command, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct)
            ?? throw new NotFoundException("Política de SLA", "default");
        policy.SetTargets(command.Targets.Select(t => new SlaTarget(
            t.Priority.ToDomain(), TimeSpan.FromMinutes(t.FirstResponseMinutes), TimeSpan.FromMinutes(t.ResolutionMinutes))));
        await db.SaveChangesAsync(ct);
        return policy.ToDto();
    }
}

// ───────────────────────────── Configuración del tenant ─────────────────────────────

[AuthorizeRoles(UserRole.Admin)]
public sealed record GetTenantSettingsQuery : IQuery<TenantSettingsDto>;

internal sealed class GetTenantSettingsHandler(IApplicationDbContext db, ITenantContext tenant) : IQueryHandler<GetTenantSettingsQuery, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> Handle(GetTenantSettingsQuery query, CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenant.RequiredTenantId, ct)
            ?? throw new NotFoundException("Tenant", tenant.RequiredTenantId);
        return t.ToDto();
    }
}

[AuthorizeRoles(UserRole.Admin)]
public sealed record UpdateTenantSettingsCommand(UpdateTenantSettingsRequest Settings) : ICommand<TenantSettingsDto>;

internal sealed class UpdateTenantSettingsValidator : AbstractValidator<UpdateTenantSettingsCommand>
{
    public UpdateTenantSettingsValidator()
    {
        RuleFor(c => c.Settings.Categories).NotEmpty().Must(c => c.Count <= Tenant.MaxCategories)
            .WithMessage($"Máximo {Tenant.MaxCategories} categorías.").OverridePropertyName("categories");
        RuleFor(c => c.Settings.AutoApplyThreshold).InclusiveBetween(0m, 1m).OverridePropertyName("autoApplyThreshold");
        RuleFor(c => c.Settings.MonthlyTokenBudget).GreaterThanOrEqualTo(0).OverridePropertyName("monthlyTokenBudget");
    }
}

internal sealed class UpdateTenantSettingsHandler(IApplicationDbContext db, ITenantContext tenant) : ICommandHandler<UpdateTenantSettingsCommand, TenantSettingsDto>
{
    public async Task<TenantSettingsDto> Handle(UpdateTenantSettingsCommand command, CancellationToken ct)
    {
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenant.RequiredTenantId, ct)
            ?? throw new NotFoundException("Tenant", tenant.RequiredTenantId);
        var s = command.Settings;
        t.SetCategories(s.Categories);
        t.UpdateAiSettings(new TenantAiSettings(s.AutoTriageEnabled, s.AutoApplyThreshold, s.MonthlyTokenBudget));
        await db.SaveChangesAsync(ct);
        return t.ToDto();
    }
}

internal static class OrganizationMappings
{
    public static TeamDto ToDto(this Team t, int memberCount) => new(t.Id, t.Name, t.Description, t.Categories, memberCount);

    public static SlaPolicyDto ToDto(this SlaPolicy p) => new(p.Id, p.Name, p.Targets
        .OrderByDescending(t => t.Priority)
        .Select(t => new SlaTargetDto(t.Priority.ToContract(), (int)t.FirstResponse.TotalMinutes, (int)t.Resolution.TotalMinutes))
        .ToList());

    public static TenantSettingsDto ToDto(this Tenant t) => new(
        t.Id, t.Name, t.Slug, t.Categories, t.AiSettings.AutoTriageEnabled, t.AiSettings.AutoApplyThreshold, t.AiSettings.MonthlyTokenBudget);
}
