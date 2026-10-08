using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Contracts.Ai;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Users;

namespace SupportAI.Application.Features.Ai.Admin;

// ───────────────────────────── Reporte de uso y precisión ─────────────────────────────

[AuthorizeRoles(UserRole.Admin)]
public sealed record GetAiUsageQuery(DateOnly? From, DateOnly? To) : IQuery<AiUsageReportDto>;

internal sealed class GetAiUsageValidator : AbstractValidator<GetAiUsageQuery>
{
    public GetAiUsageValidator() =>
        RuleFor(q => q).Must(q => q.From is null || q.To is null || (q.From <= q.To && q.To.Value.DayNumber - q.From.Value.DayNumber <= 366))
            .WithMessage("Rango de fechas inválido (máximo 1 año).");
}

internal sealed class GetAiUsageHandler(IApplicationDbContext db, ITenantContext tenant, TokenBudgetGuard budget, TimeProvider clock)
    : IQueryHandler<GetAiUsageQuery, AiUsageReportDto>
{
    public async Task<AiUsageReportDto> Handle(GetAiUsageQuery query, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-29);
        var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toTs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var tenantId = tenant.RequiredTenantId;

        // Agregación en SQL por día y operación (GROUP BY sobre la fecha UTC).
        var calls = await db.LlmCalls.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.CreatedAt >= fromTs && l.CreatedAt < toTs)
            .GroupBy(l => new { l.CreatedAt.Year, l.CreatedAt.Month, l.CreatedAt.Day, l.Operation })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Operation,
                Calls = g.Count(),
                Failures = g.Count(l => !l.Succeeded),
                Input = g.Sum(l => l.InputTokens + l.CacheCreationInputTokens),
                Output = g.Sum(l => l.OutputTokens),
                CacheRead = g.Sum(l => l.CacheReadInputTokens),
                Cost = g.Sum(l => l.EstimatedCostUsd),
                AvgLatency = g.Average(l => (double)l.LatencyMs),
            })
            .ToListAsync(ct);

        var days = calls
            .Select(c => new AiUsageDayDto(new DateOnly(c.Year, c.Month, c.Day), c.Operation, c.Calls, c.Failures, c.Input, c.Output, c.CacheRead, c.Cost, Math.Round(c.AvgLatency, 1)))
            .OrderBy(d => d.Day).ThenBy(d => d.Operation)
            .ToList();

        var accuracy = await db.TriageResults.AsNoTracking()
            .GroupBy(r => new { r.PromptKey, r.PromptVersion })
            .Select(g => new
            {
                g.Key.PromptKey,
                g.Key.PromptVersion,
                Total = g.Count(),
                AutoApplied = g.Count(r => r.Status == TriageResultStatus.AutoApplied),
                Accepted = g.Count(r => r.Status == TriageResultStatus.Accepted),
                Overridden = g.Count(r => r.Status == TriageResultStatus.Overridden),
                Invalid = g.Count(r => r.Status == TriageResultStatus.Invalid),
            })
            .ToListAsync(ct);

        var (monthTokens, monthBudget) = await budget.GetMonthUsageAsync(ct);
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthCost = await db.LlmCalls.Where(l => l.TenantId == tenantId && l.CreatedAt >= monthStart).SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;

        var totalInput = days.Sum(d => d.InputTokens) + days.Sum(d => d.CacheReadTokens);
        var cacheHitRatio = totalInput == 0 ? 0 : Math.Round((double)days.Sum(d => d.CacheReadTokens) / totalInput, 4);

        return new AiUsageReportDto(from, to, monthTokens, monthBudget, monthCost, cacheHitRatio, days,
            accuracy.OrderBy(a => a.PromptKey).ThenBy(a => a.PromptVersion).Select(a =>
            {
                var reviewed = a.Accepted + a.Overridden;
                // Tasa de aceptación: sobre lo revisado por humanos; los auto-aplicados no corregidos cuentan como aciertos.
                var denominator = reviewed + a.AutoApplied;
                var rate = denominator == 0 ? 0 : Math.Round((double)(a.Accepted + a.AutoApplied) / denominator, 4);
                return new PromptAccuracyDto(a.PromptKey, a.PromptVersion, a.Total, a.AutoApplied, a.Accepted, a.Overridden, a.Invalid, rate);
            }).ToList());
    }
}

// ───────────────────────────── Versiones de prompts ─────────────────────────────

[AuthorizeRoles(UserRole.Admin)]
public sealed record GetPromptTemplatesQuery : IQuery<IReadOnlyList<PromptTemplateDto>>;

internal sealed class GetPromptTemplatesHandler(IApplicationDbContext db) : IQueryHandler<GetPromptTemplatesQuery, IReadOnlyList<PromptTemplateDto>>
{
    public async Task<IReadOnlyList<PromptTemplateDto>> Handle(GetPromptTemplatesQuery query, CancellationToken ct)
    {
        var templates = await db.PromptTemplates.AsNoTracking().OrderBy(p => p.Key).ThenByDescending(p => p.Version).ToListAsync(ct);
        return templates.Select(t => t.ToDto()).ToList();
    }
}

[AuthorizeRoles(UserRole.Admin)]
public sealed record GetPromptTemplateQuery(Guid Id) : IQuery<PromptTemplateDetailDto>;

internal sealed class GetPromptTemplateHandler(IApplicationDbContext db) : IQueryHandler<GetPromptTemplateQuery, PromptTemplateDetailDto>
{
    public async Task<PromptTemplateDetailDto> Handle(GetPromptTemplateQuery query, CancellationToken ct)
    {
        var t = await db.PromptTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.Id, ct)
            ?? throw new NotFoundException("Prompt", query.Id);
        return new PromptTemplateDetailDto(t.ToDto(), t.SystemPrompt, t.UserTemplate, t.JsonSchema);
    }
}

/// <summary>Activa una versión (y desactiva la anterior) en una transacción. Revertir = activar la versión previa.</summary>
[AuthorizeRoles(UserRole.Admin)]
public sealed record ActivatePromptVersionCommand(string Key, int Version) : ICommand<PromptTemplateDto>;

internal sealed class ActivatePromptVersionHandler(IApplicationDbContext db, TimeProvider clock)
    : ICommandHandler<ActivatePromptVersionCommand, PromptTemplateDto>
{
    public async Task<PromptTemplateDto> Handle(ActivatePromptVersionCommand command, CancellationToken ct)
    {
        var target = await db.PromptTemplates.FirstOrDefaultAsync(p => p.Key == command.Key && p.Version == command.Version, ct)
            ?? throw new NotFoundException("Prompt", $"{command.Key} v{command.Version}");
        if (target.IsActive)
        {
            return target.ToDto();
        }

        // Dos pasos dentro de una transacción: el índice único filtrado (una sola versión activa por clave)
        // exige desactivar la actual antes de activar la nueva.
        await db.ExecuteInTransactionAsync(async token =>
        {
            var current = await db.PromptTemplates.Where(p => p.Key == command.Key && p.IsActive).ToListAsync(token);
            current.ForEach(p => p.Deactivate());
            await db.SaveChangesAsync(token);

            target.Activate(clock.GetUtcNow());
            await db.SaveChangesAsync(token);
        }, ct);

        return target.ToDto();
    }
}
