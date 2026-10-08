using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Common.Mapping;
using SupportAI.Contracts.Tickets;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;
using C = SupportAI.Contracts.Common;

namespace SupportAI.Application.Features.Tickets;

// ───────────────────────────── Crear ticket ─────────────────────────────

public sealed record CreateTicketCommand(string Subject, string Description, C.TicketChannel Channel) : ICommand<TicketCreatedResult>;

public sealed record TicketCreatedResult(Guid Id, long Number);

internal sealed class CreateTicketValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketValidator()
    {
        RuleFor(c => c.Subject).NotEmpty().MaximumLength(Ticket.MaxSubjectLength);
        RuleFor(c => c.Description).NotEmpty().MaximumLength(Ticket.MaxDescriptionLength);
        RuleFor(c => c.Channel).IsInEnum();
    }
}

internal sealed class CreateTicketHandler(IApplicationDbContext db, ITenantContext tenant, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<CreateTicketCommand, TicketCreatedResult>
{
    public async Task<TicketCreatedResult> Handle(CreateTicketCommand command, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);
        var ticket = Ticket.Create(
            tenant.RequiredTenantId, user.RequiredUserId, command.Subject, command.Description,
            command.Channel.ToDomain(), policy, clock.GetUtcNow());

        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        // Number lo asigna la SEQUENCE de SQL Server; EF lo lee de vuelta tras el INSERT.
        return new TicketCreatedResult(ticket.Id, ticket.Number);
    }
}

// ───────────────────────────── Agregar mensaje ─────────────────────────────

public sealed record AddTicketMessageCommand(Guid TicketId, string Body, bool IsInternal) : ICommand<TicketMessageDto>;

internal sealed class AddTicketMessageValidator : AbstractValidator<AddTicketMessageCommand>
{
    public AddTicketMessageValidator()
    {
        RuleFor(c => c.TicketId).NotEmpty();
        RuleFor(c => c.Body).NotEmpty().MaximumLength(TicketMessage.MaxBodyLength);
    }
}

internal sealed class AddTicketMessageHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<AddTicketMessageCommand, TicketMessageDto>
{
    public async Task<TicketMessageDto> Handle(AddTicketMessageCommand command, CancellationToken ct)
    {
        var ticket = await db.LoadForUpdateAsync(user, command.TicketId, ct);
        var authorType = user.IsStaff ? MessageAuthorType.Agent : MessageAuthorType.Customer;

        var message = ticket.AddMessage(user.RequiredUserId, authorType, command.Body, command.IsInternal, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var authorName = await db.Members.Where(m => m.Id == user.RequiredUserId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct);
        return new TicketMessageDto(message.Id, message.AuthorId, authorName, message.AuthorType.ToContract(), message.Body, message.IsInternal, message.CreatedAt);
    }
}

// ───────────────────────────── Cambiar estado ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record ChangeTicketStatusCommand(Guid TicketId, C.TicketStatus Status, string? ExpectedRowVersion = null) : ICommand;

internal sealed class ChangeTicketStatusValidator : AbstractValidator<ChangeTicketStatusCommand>
{
    public ChangeTicketStatusValidator()
    {
        RuleFor(c => c.TicketId).NotEmpty();
        RuleFor(c => c.Status).IsInEnum();
    }
}

internal sealed class ChangeTicketStatusHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<ChangeTicketStatusCommand>
{
    public async Task<Unit> Handle(ChangeTicketStatusCommand command, CancellationToken ct)
    {
        var ticket = await db.LoadForUpdateAsync(user, command.TicketId, ct);
        ticket.EnsureVersion(command.ExpectedRowVersion);
        ticket.ChangeStatus(command.Status.ToDomain(), user.RequiredUserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ───────────────────────────── Cambiar prioridad ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record ChangeTicketPriorityCommand(Guid TicketId, C.TicketPriority Priority, string? ExpectedRowVersion = null) : ICommand;

internal sealed class ChangeTicketPriorityValidator : AbstractValidator<ChangeTicketPriorityCommand>
{
    public ChangeTicketPriorityValidator()
    {
        RuleFor(c => c.TicketId).NotEmpty();
        RuleFor(c => c.Priority).IsInEnum();
    }
}

internal sealed class ChangeTicketPriorityHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<ChangeTicketPriorityCommand>
{
    public async Task<Unit> Handle(ChangeTicketPriorityCommand command, CancellationToken ct)
    {
        var ticket = await db.LoadForUpdateAsync(user, command.TicketId, ct);
        ticket.EnsureVersion(command.ExpectedRowVersion);
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);
        ticket.ChangePriority(command.Priority.ToDomain(), policy, user.RequiredUserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ───────────────────────────── Asignar ─────────────────────────────

[AuthorizeRoles(UserRole.Agent, UserRole.Admin)]
public sealed record AssignTicketCommand(Guid TicketId, Guid? AssigneeId, Guid? TeamId, string? ExpectedRowVersion = null) : ICommand;

internal sealed class AssignTicketValidator : AbstractValidator<AssignTicketCommand>
{
    public AssignTicketValidator() => RuleFor(c => c.TicketId).NotEmpty();
}

internal sealed class AssignTicketHandler(IApplicationDbContext db, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<AssignTicketCommand>
{
    public async Task<Unit> Handle(AssignTicketCommand command, CancellationToken ct)
    {
        var ticket = await db.LoadForUpdateAsync(user, command.TicketId, ct);
        ticket.EnsureVersion(command.ExpectedRowVersion);

        if (command.AssigneeId is { } assigneeId &&
            !await db.Members.AnyAsync(m => m.Id == assigneeId && m.IsActive && m.Role != UserRole.Customer, ct))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.AssigneeId)] = ["El agente no existe, está inactivo o no es parte del staff."],
            });
        }

        if (command.TeamId is { } teamId && !await db.Teams.AnyAsync(t => t.Id == teamId, ct))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.TeamId)] = ["El equipo no existe."],
            });
        }

        ticket.Assign(command.AssigneeId, command.TeamId, user.RequiredUserId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
