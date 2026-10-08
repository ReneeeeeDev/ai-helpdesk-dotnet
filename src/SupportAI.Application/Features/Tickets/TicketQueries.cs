using System.Globalization;
using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Common.Mapping;
using SupportAI.Application.Common.Paging;
using SupportAI.Application.Features.Ai;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Tickets;
using SupportAI.Domain.Tickets;
using DT = SupportAI.Domain.Tickets;

namespace SupportAI.Application.Features.Tickets;

// ───────────────────────────── Inbox (lista paginada) ─────────────────────────────

public sealed record GetTicketsQuery(TicketListQuery Filter) : IQuery<CursorPage<TicketSummaryDto>>;

internal sealed class GetTicketsValidator : AbstractValidator<GetTicketsQuery>
{
    public GetTicketsValidator()
    {
        RuleFor(q => q.Filter.PageSize).InclusiveBetween(1, 100).OverridePropertyName("pageSize");
        RuleFor(q => q.Filter.Search).MaximumLength(200).OverridePropertyName("search");
        RuleFor(q => q.Filter.Sort).IsInEnum().OverridePropertyName("sort");
        RuleFor(q => q.Filter.Cursor)
            .Must(c => c is null || KeysetCursor.TryDecode(c) is not null)
            .WithMessage("Cursor inválido.")
            .OverridePropertyName("cursor");
    }
}

/// <summary>Fila intermedia con tipos de dominio; el mapeo a DTO se hace en memoria (los enums se guardan como texto).</summary>
internal sealed record TicketRow(
    Guid Id, long Number, string Subject, DT.TicketStatus Status, DT.TicketPriority Priority, DT.TicketChannel Channel,
    string? Category, DT.Sentiment? Sentiment, Guid RequesterId, string? RequesterName, Guid? AssigneeId, string? AssigneeName,
    Guid? TeamId, string? TeamName, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? FirstResponseDueAt,
    DateTimeOffset? ResolutionDueAt, bool SlaBreached, bool NeedsHumanReview, int MessageCount)
{
    public TicketSummaryDto ToDto() => new(
        Id, Number, Subject, Status.ToContract(), Priority.ToContract(), Channel.ToContract(), Category, Sentiment.ToContract(),
        RequesterId, RequesterName, AssigneeId, AssigneeName, TeamId, TeamName, CreatedAt, UpdatedAt, FirstResponseDueAt,
        ResolutionDueAt, SlaBreached, NeedsHumanReview, MessageCount);
}

internal static class TicketProjections
{
    public static IQueryable<TicketRow> SelectRows(this IQueryable<Ticket> tickets, IApplicationDbContext db, bool isStaff) =>
        tickets.Select(t => new TicketRow(
            t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Channel, t.Category, t.Sentiment,
            t.RequesterId,
            db.Members.Where(m => m.Id == t.RequesterId).Select(m => m.DisplayName).FirstOrDefault(),
            t.AssigneeId,
            db.Members.Where(m => m.Id == t.AssigneeId).Select(m => m.DisplayName).FirstOrDefault(),
            t.TeamId,
            db.Teams.Where(team => team.Id == t.TeamId).Select(team => team.Name).FirstOrDefault(),
            t.CreatedAt, t.UpdatedAt, t.FirstResponseDueAt, t.ResolutionDueAt,
            t.FirstResponseBreached || t.ResolutionBreached,
            t.NeedsHumanReview,
            t.Messages.Count(m => isStaff || !m.IsInternal)));
}

internal sealed class GetTicketsHandler(IApplicationDbContext db, ICurrentUser user)
    : IQueryHandler<GetTicketsQuery, CursorPage<TicketSummaryDto>>
{
    private static readonly DateTimeOffset NoDueDate = DateTimeOffset.MaxValue;

    public async Task<CursorPage<TicketSummaryDto>> Handle(GetTicketsQuery query, CancellationToken ct)
    {
        var f = query.Filter;
        var filtered = ApplyFilters(db.Tickets.AsNoTracking().VisibleTo(user), f);
        var total = await filtered.CountAsync(ct);

        var cursor = KeysetCursor.TryDecode(f.Cursor);
        var page = ApplySortAndCursor(filtered, f.Sort, cursor);

        var rows = await page.Take(f.PageSize + 1).SelectRows(db, user.IsStaff).ToListAsync(ct);
        var hasMore = rows.Count > f.PageSize;
        var items = rows.Take(f.PageSize).ToList();

        var nextCursor = hasMore ? BuildCursor(items[^1], f.Sort).Encode() : null;
        return new CursorPage<TicketSummaryDto>(items.Select(r => r.ToDto()).ToList(), nextCursor, total);
    }

    private IQueryable<Ticket> ApplyFilters(IQueryable<Ticket> q, TicketListQuery f)
    {
        if (f.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(t => t.Status == s);
        }
        else if (!f.IncludeClosed)
        {
            q = q.Where(t => t.Status != DT.TicketStatus.Closed);
        }

        if (f.Priority is { } priority)
        {
            var p = priority.ToDomain();
            q = q.Where(t => t.Priority == p);
        }

        if (user.IsStaff)
        {
            if (f.OnlyMine)
            {
                var me = user.RequiredUserId;
                q = q.Where(t => t.AssigneeId == me);
            }
            else if (f.AssigneeId is { } assignee)
            {
                q = q.Where(t => t.AssigneeId == assignee);
            }

            if (f.TeamId is { } team)
            {
                q = q.Where(t => t.TeamId == team);
            }

            if (f.NeedsReview is { } needsReview)
            {
                q = q.Where(t => t.NeedsHumanReview == needsReview);
            }
        }

        if (!string.IsNullOrWhiteSpace(f.Category))
        {
            var category = f.Category.Trim().ToLowerInvariant();
            q = q.Where(t => t.Category == category);
        }

        if (f.SlaBreached is { } breached)
        {
            q = breached
                ? q.Where(t => t.FirstResponseBreached || t.ResolutionBreached)
                : q.Where(t => !t.FirstResponseBreached && !t.ResolutionBreached);
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            if (long.TryParse(term.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                q = q.Where(t => t.Number == number);
            }
            else
            {
                q = q.Where(t => t.Subject.Contains(term) || t.Description.Contains(term));
            }
        }

        return q;
    }

    private static IQueryable<Ticket> ApplySortAndCursor(IQueryable<Ticket> q, TicketSort sort, KeysetCursor? c)
    {
        switch (sort)
        {
            case TicketSort.Oldest:
                if (c is not null)
                {
                    q = q.Where(t => t.Number > c.Number);
                }

                return q.OrderBy(t => t.Number);

            case TicketSort.Priority:
                if (c is { Key: { } key })
                {
                    var p = (DT.TicketPriority)(int)key;
                    q = q.Where(t => t.Priority < p || (t.Priority == p && t.Number < c.Number));
                }

                return q.OrderByDescending(t => t.Priority).ThenByDescending(t => t.Number);

            case TicketSort.DueSoon:
                Expression<Func<Ticket, DateTimeOffset>> due = t => t.ResolutionDueAt ?? NoDueDate;
                if (c is { Key: { } ticks })
                {
                    var keyDue = new DateTimeOffset(ticks, TimeSpan.Zero);
                    q = q.Where(t => (t.ResolutionDueAt ?? NoDueDate) > keyDue
                        || ((t.ResolutionDueAt ?? NoDueDate) == keyDue && t.Number > c.Number));
                }

                return q.OrderBy(due).ThenBy(t => t.Number);

            default:
                if (c is not null)
                {
                    q = q.Where(t => t.Number < c.Number);
                }

                return q.OrderByDescending(t => t.Number);
        }
    }

    private static KeysetCursor BuildCursor(TicketRow last, TicketSort sort) => sort switch
    {
        TicketSort.Priority => new KeysetCursor(last.Number, (int)last.Priority),
        TicketSort.DueSoon => new KeysetCursor(last.Number, (last.ResolutionDueAt ?? NoDueDate).UtcTicks),
        _ => new KeysetCursor(last.Number, null),
    };
}

// ───────────────────────────── Detalle ─────────────────────────────

public sealed record GetTicketByIdQuery(Guid TicketId) : IQuery<TicketDetailDto>;

internal sealed class GetTicketByIdHandler(IApplicationDbContext db, ICurrentUser user)
    : IQueryHandler<GetTicketByIdQuery, TicketDetailDto>
{
    public async Task<TicketDetailDto> Handle(GetTicketByIdQuery query, CancellationToken ct)
    {
        var isStaff = user.IsStaff;
        var ticket = await db.Tickets.AsNoTracking().VisibleTo(user)
            .Include(t => t.Messages.Where(m => isStaff || !m.IsInternal).OrderBy(m => m.CreatedAt))
            .Include(t => t.Activities.OrderBy(a => a.OccurredAt))
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == query.TicketId, ct)
            ?? throw new NotFoundException("Ticket", query.TicketId);

        var summary = await db.Tickets.AsNoTracking().Where(t => t.Id == ticket.Id).SelectRows(db, isStaff).SingleAsync(ct);

        var userIds = ticket.Messages.Select(m => m.AuthorId)
            .Concat(ticket.Activities.Select(a => a.ActorId))
            .OfType<Guid>().Distinct().ToList();
        var names = await db.Members.AsNoTracking().Where(m => userIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName, ct);

        var messages = ticket.Messages
            .Select(m => new TicketMessageDto(m.Id, m.AuthorId, m.AuthorId is { } a ? names.GetValueOrDefault(a) : null,
                m.AuthorType.ToContract(), m.Body, m.IsInternal, m.CreatedAt))
            .ToList();

        // El historial y el triage de IA son información interna: solo para el staff.
        var activities = isStaff
            ? ticket.Activities.Select(a => new TicketActivityDto(a.Type.ToString(), a.Description, a.ActorId,
                a.ActorId is { } id ? names.GetValueOrDefault(id) : null, a.OccurredAt)).ToList()
            : [];

        var latestTriage = isStaff
            ? await db.TriageResults.AsNoTracking().Where(r => r.TicketId == ticket.Id).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct)
            : null;

        var transitions = isStaff
            ? TicketStateMachine.AllowedFrom(ticket.Status).Select(s => s.ToContract()).ToList()
            : [];

        return new TicketDetailDto(
            summary.ToDto(),
            ticket.Description,
            ticket.Language,
            isStaff ? ticket.AiSummary : null,
            ticket.Tags,
            ticket.FirstRespondedAt,
            ticket.ResolvedAt,
            ticket.SlaPausedAt,
            messages,
            activities,
            transitions,
            latestTriage?.ToDto(),
            Convert.ToBase64String(ticket.RowVersion));
    }
}
