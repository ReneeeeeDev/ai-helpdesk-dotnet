using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Events;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Common.Mapping;
using SupportAI.Contracts.Ai;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Tickets.Events;
using SupportAI.Domain.Users;
using C = SupportAI.Contracts.Common;

namespace SupportAI.Application.Features.Ai.Triage;

/// <summary>
/// Triage automático al crear un ticket (vía outbox). Errores transitorios del LLM se relanzan para que el
/// outbox reintente con backoff; los definitivos dejan el ticket marcado para revisión humana.
/// </summary>
internal sealed class TriageOnTicketCreated(TicketTriageService triage, IApplicationDbContext db, TimeProvider clock)
    : IDomainEventHandler<TicketCreated>
{
    public async Task Handle(TicketCreated domainEvent, CancellationToken ct)
    {
        try
        {
            await triage.TriageAsync(domainEvent.TicketId, manual: false, ct);
        }
        catch (LlmException ex) when (!ex.IsTransient)
        {
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == domainEvent.TicketId, ct);
            ticket?.FlagForHumanReview($"triage de IA no disponible ({ex.Kind})", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
    }
}

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record RetriageTicketCommand(Guid TicketId) : ICommand<TriageResultDto>;

internal sealed class RetriageTicketHandler(TicketTriageService triage, IApplicationDbContext db)
    : ICommandHandler<RetriageTicketCommand, TriageResultDto>
{
    public async Task<TriageResultDto> Handle(RetriageTicketCommand command, CancellationToken ct)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == command.TicketId, ct))
        {
            throw new NotFoundException("Ticket", command.TicketId);
        }

        var result = await triage.TriageAsync(command.TicketId, manual: true, ct)
            ?? throw new ConflictException("El ticket está cerrado: no se puede volver a clasificar.", "ticket_closed");
        return result.ToDto();
    }
}

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record GetTriageHistoryQuery(Guid TicketId) : IQuery<IReadOnlyList<TriageResultDto>>;

internal sealed class GetTriageHistoryHandler(IApplicationDbContext db) : IQueryHandler<GetTriageHistoryQuery, IReadOnlyList<TriageResultDto>>
{
    public async Task<IReadOnlyList<TriageResultDto>> Handle(GetTriageHistoryQuery query, CancellationToken ct)
    {
        var results = await db.TriageResults.AsNoTracking().Where(r => r.TicketId == query.TicketId)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return results.Select(r => r.ToDto()).ToList();
    }
}

/// <summary>El agente confirma el triage de IA (señal positiva para la métrica de precisión del prompt).</summary>
[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record AcceptTriageCommand(Guid TicketId, Guid ResultId) : ICommand<TriageResultDto>;

internal sealed class AcceptTriageHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<AcceptTriageCommand, TriageResultDto>
{
    public async Task<TriageResultDto> Handle(AcceptTriageCommand command, CancellationToken ct)
    {
        var (ticket, result) = await TriageReview.LoadAsync(db, command.TicketId, command.ResultId, ct);
        var now = clock.GetUtcNow();
        var wasApplied = result.Status == TriageResultStatus.AutoApplied;

        result.Accept(user.RequiredUserId, now);
        if (!wasApplied)
        {
            var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);
            ticket.ApplyTriage(result.ToDecision(), policy, user.RequiredUserId, byAi: true, now);
        }

        ticket.RecordTriageReview(accepted: true, user.RequiredUserId, now);
        await db.SaveChangesAsync(ct);
        return result.ToDto();
    }
}

/// <summary>El agente corrige el triage: se aplica su decisión y el resultado de IA queda como Overridden.</summary>
[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record OverrideTriageCommand(Guid TicketId, Guid ResultId, string? Category, C.TicketPriority Priority, Guid? TeamId, string? Notes)
    : ICommand<TriageResultDto>;

internal sealed class OverrideTriageValidator : AbstractValidator<OverrideTriageCommand>
{
    public OverrideTriageValidator()
    {
        RuleFor(c => c.Priority).IsInEnum();
        RuleFor(c => c.Category).MaximumLength(60);
        RuleFor(c => c.Notes).MaximumLength(1000);
    }
}

internal sealed class OverrideTriageHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<OverrideTriageCommand, TriageResultDto>
{
    public async Task<TriageResultDto> Handle(OverrideTriageCommand command, CancellationToken ct)
    {
        var (ticket, result) = await TriageReview.LoadAsync(db, command.TicketId, command.ResultId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == ticket.TenantId, ct);
        if (command.Category is not null && !tenant.HasCategory(command.Category))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["category"] = ["La categoría no existe en la taxonomía del tenant."] });
        }

        if (command.TeamId is { } teamId && !await db.Teams.AnyAsync(t => t.Id == teamId, ct))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["teamId"] = ["El equipo no existe."] });
        }

        var now = clock.GetUtcNow();
        result.Override(user.RequiredUserId, command.Notes, now);

        var ai = result.ToDecision();
        var decision = ai with { Category = command.Category ?? ai.Category, Priority = command.Priority.ToDomain(), TeamId = command.TeamId ?? ai.TeamId };
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);
        ticket.ApplyTriage(decision, policy, user.RequiredUserId, byAi: false, now);
        ticket.RecordTriageReview(accepted: false, user.RequiredUserId, now);

        await db.SaveChangesAsync(ct);
        return result.ToDto();
    }
}

internal static class TriageReview
{
    public static async Task<(Ticket Ticket, AiTriageResult Result)> LoadAsync(IApplicationDbContext db, Guid ticketId, Guid resultId, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException("Ticket", ticketId);
        var result = await db.TriageResults.FirstOrDefaultAsync(r => r.Id == resultId && r.TicketId == ticketId, ct)
            ?? throw new NotFoundException("Resultado de triage", resultId);
        if (result.Status == TriageResultStatus.Invalid)
        {
            throw new ConflictException("Un resultado inválido no puede aceptarse ni corregirse; vuelve a ejecutar el triage.", "triage_invalid");
        }

        return (ticket, result);
    }
}
