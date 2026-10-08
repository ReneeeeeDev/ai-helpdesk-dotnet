using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Domain.Ai;

namespace SupportAI.Application.Features.Ai.Llm;

/// <summary>Precio por millón de tokens de un modelo (USD).</summary>
public sealed class ModelPricing
{
    public decimal InputPerMTok { get; set; }

    public decimal OutputPerMTok { get; set; }

    public decimal CacheWritePerMTok { get; set; }

    public decimal CacheReadPerMTok { get; set; }
}

public sealed class LlmPricingOptions
{
    public const string SectionName = "Llm:Pricing";

    public Dictionary<string, ModelPricing> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal Estimate(string model, LlmUsage usage)
    {
        if (!Models.TryGetValue(model, out var price))
        {
            return 0m;
        }

        const decimal million = 1_000_000m;
        return (usage.InputTokens * price.InputPerMTok
              + usage.OutputTokens * price.OutputPerMTok
              + usage.CacheCreationInputTokens * price.CacheWritePerMTok
              + usage.CacheReadInputTokens * price.CacheReadPerMTok) / million;
    }
}

/// <summary>Contexto de negocio de una llamada (para trazabilidad en LlmCallLog).</summary>
public sealed record LlmCallContext(string Operation, Guid? TicketId, string? PromptKey, int? PromptVersion);

/// <summary>Registro de cada llamada al LLM (éxito o error) con su costo estimado.</summary>
public interface ILlmCallRecorder
{
    Task<Guid> RecordSuccessAsync(LlmCallContext ctx, LlmResponse response, int attempt, CancellationToken ct);

    Task RecordFailureAsync(LlmCallContext ctx, string model, Exception error, TimeSpan latency, int attempt, CancellationToken ct);
}

/// <summary>Persiste un <see cref="LlmCallLog"/> por cada llamada (éxito o error) con su costo estimado.</summary>
public sealed class LlmCallRecorder(IApplicationDbContext db, ITenantContext tenant, IOptions<LlmPricingOptions> pricing, TimeProvider clock)
    : ILlmCallRecorder
{
    public async Task<Guid> RecordSuccessAsync(LlmCallContext ctx, LlmResponse response, int attempt, CancellationToken ct)
    {
        var log = LlmCallLog.Success(
            tenant.TenantId, ctx.TicketId, ctx.Operation, ctx.PromptKey, ctx.PromptVersion, response.Model, response.Usage,
            (long)response.Latency.TotalMilliseconds, response.StopReason, pricing.Value.Estimate(response.Model, response.Usage),
            Activity.Current?.TraceId.ToString(), attempt, clock.GetUtcNow());
        db.LlmCalls.Add(log);
        await db.SaveChangesAsync(ct);
        return log.Id;
    }

    public async Task RecordFailureAsync(LlmCallContext ctx, string model, Exception error, TimeSpan latency, int attempt, CancellationToken ct)
    {
        var kind = error is LlmException llm ? llm.Kind.ToString() : error.GetType().Name;
        db.LlmCalls.Add(LlmCallLog.Failure(
            tenant.TenantId, ctx.TicketId, ctx.Operation, ctx.PromptKey, ctx.PromptVersion, model, kind, error.Message,
            (long)latency.TotalMilliseconds, Activity.Current?.TraceId.ToString(), attempt, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Impide superar el presupuesto mensual de tokens del tenant (→ 429 en la API).</summary>
public sealed class TokenBudgetGuard(IApplicationDbContext db, ITenantContext tenant, TimeProvider clock)
{
    public async Task<(long Used, long Budget)> GetMonthUsageAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequiredTenantId;
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var budget = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.AiSettings.MonthlyTokenBudget).FirstAsync(ct);
        var used = await db.LlmCalls
            .Where(l => l.TenantId == tenantId && l.CreatedAt >= monthStart)
            .SumAsync(l => (long?)(l.InputTokens + l.OutputTokens + l.CacheCreationInputTokens + l.CacheReadInputTokens), ct) ?? 0;
        return (used, budget);
    }

    public async Task EnsureWithinBudgetAsync(CancellationToken ct)
    {
        var (used, budget) = await GetMonthUsageAsync(ct);
        if (budget > 0 && used >= budget)
        {
            var now = clock.GetUtcNow();
            var nextMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
            throw new QuotaExceededException(
                $"Se agotó el presupuesto mensual de IA del tenant ({used:N0}/{budget:N0} tokens).", nextMonth - now);
        }
    }
}
