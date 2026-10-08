using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Triage;
using SupportAI.Domain.Ai;

namespace SupportAI.PromptEvals;

public sealed class EvalOptions
{
    public string Provider { get; private set; } = "fake";

    public int? Version { get; private set; }

    public string? Model { get; private set; }

    public string Dataset { get; private set; } = "evals/triage-golden.jsonl";

    public string OutputDirectory { get; private set; } = "eval-results";

    public double MinCategoryAccuracy { get; private set; } = 0.8;

    public int Concurrency { get; private set; } = 4;

    public int MaxCases { get; private set; } = int.MaxValue;

    public static EvalOptions Parse(string[] args)
    {
        var o = new EvalOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}");
            switch (args[i])
            {
                case "--provider": o.Provider = Next().ToLowerInvariant(); break;
                case "--version": o.Version = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--model": o.Model = Next(); break;
                case "--dataset": o.Dataset = Next(); break;
                case "--out": o.OutputDirectory = Next(); break;
                case "--min-category-accuracy": o.MinCategoryAccuracy = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--concurrency": o.Concurrency = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--max-cases": o.MaxCases = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Argumento desconocido: {args[i]}");
            }
        }

        return o;
    }
}

/// <summary>Tenant ficticio de evaluación (misma taxonomía que el tenant demo).</summary>
public static class EvalTenant
{
    public static readonly IReadOnlyList<string> Categories =
        ["acceso", "facturacion", "rendimiento", "integraciones", "cuenta", "bug", "consulta-general"];

    public static readonly IReadOnlyList<(string Name, IReadOnlyList<string> Categories)> Teams =
    [
        ("Soporte Técnico", ["acceso", "rendimiento", "bug", "integraciones"]),
        ("Facturación", ["facturacion", "cuenta"]),
        ("Atención General", ["consulta-general"]),
    ];
}

public static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public sealed record GoldenExpectation(string Category, TriagePriority Priority, string? Team, bool? RequiresHuman, bool? ContainsPii);

public sealed record GoldenCase(string Id, string Subject, string Description, string? Channel, GoldenExpectation Expected);

public sealed record CaseResult(
    string Id,
    bool SchemaValid,
    int Attempts,
    string? Error,
    string ExpectedCategory,
    string? ActualCategory,
    TriagePriority ExpectedPriority,
    TriagePriority? ActualPriority,
    string? ExpectedTeam,
    string? ActualTeam,
    bool? ExpectedRequiresHuman,
    bool? ActualRequiresHuman,
    bool? ExpectedPii,
    bool? ActualPii,
    decimal? Confidence)
{
    public bool CategoryOk => ActualCategory == ExpectedCategory;

    public bool PriorityOk => ActualPriority == ExpectedPriority;

    public bool PriorityWithinOne => ActualPriority is { } p && Math.Abs((int)p - (int)ExpectedPriority) <= 1;

    public bool TeamOk => ExpectedTeam is null || string.Equals(ActualTeam, ExpectedTeam, StringComparison.OrdinalIgnoreCase);

    public bool? HumanOk => ExpectedRequiresHuman is null ? null : ActualRequiresHuman == ExpectedRequiresHuman;

    public bool? PiiOk => ExpectedPii is null ? null : ActualPii == ExpectedPii;

    public static CaseResult From(GoldenCase c, TriageOutput? o, bool valid, int attempts, string errors, bool redactedPii) => new(
        c.Id, valid && o is not null, attempts, valid ? null : errors,
        c.Expected.Category, o?.Category, c.Expected.Priority, o?.Priority, c.Expected.Team, o?.SuggestedTeam,
        c.Expected.RequiresHuman, o?.RequiresHuman, c.Expected.ContainsPii, o is null ? null : o.ContainsPii || redactedPii, o?.Confidence);

    public static CaseResult Failed(GoldenCase c, string error) => new(
        c.Id, false, 0, error, c.Expected.Category, null, c.Expected.Priority, null, c.Expected.Team, null,
        c.Expected.RequiresHuman, null, c.Expected.ContainsPii, null, null);
}

/// <summary>Acumula uso y costo de las llamadas de la evaluación (no persiste nada).</summary>
public sealed class InMemoryRecorder(LlmPricingOptions pricing) : ILlmCallRecorder
{
    private readonly Lock _lock = new();

    public LlmUsage Usage { get; private set; } = LlmUsage.Empty;

    public decimal CostUsd { get; private set; }

    public int Calls { get; private set; }

    public int Failures { get; private set; }

    public Task<Guid> RecordSuccessAsync(LlmCallContext ctx, LlmResponse response, int attempt, CancellationToken ct)
    {
        lock (_lock)
        {
            Usage += response.Usage;
            CostUsd += pricing.Estimate(response.Model, response.Usage);
            Calls++;
        }

        return Task.FromResult(Guid.CreateVersion7());
    }

    public Task RecordFailureAsync(LlmCallContext ctx, string model, Exception error, TimeSpan latency, int attempt, CancellationToken ct)
    {
        lock (_lock)
        {
            Failures++;
        }

        return Task.CompletedTask;
    }
}

public sealed record EvalReport(
    string PromptKey,
    int PromptVersion,
    string Model,
    string Effort,
    string Provider,
    int Cases,
    double CategoryAccuracy,
    double PriorityAccuracy,
    double PriorityWithinOneAccuracy,
    double TeamAccuracy,
    double? RequiresHumanAccuracy,
    double? PiiAccuracy,
    double SchemaValidRate,
    double RepairRate,
    int Calls,
    int Failures,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    double ElapsedSeconds,
    IReadOnlyList<CaseResult> Results)
{
    public static EvalReport Create(PromptDefinition prompt, string provider, IReadOnlyList<CaseResult> results, InMemoryRecorder recorder, TimeSpan elapsed)
    {
        static double Rate(IEnumerable<bool> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? 0 : (double)list.Count(v => v) / list.Count;
        }

        var human = results.Select(r => r.HumanOk).OfType<bool>().ToList();
        var pii = results.Select(r => r.PiiOk).OfType<bool>().ToList();
        return new EvalReport(
            prompt.Key, prompt.Version, prompt.Model, prompt.Effort, provider, results.Count,
            Rate(results.Select(r => r.CategoryOk)),
            Rate(results.Select(r => r.PriorityOk)),
            Rate(results.Select(r => r.PriorityWithinOne)),
            Rate(results.Select(r => r.TeamOk)),
            human.Count == 0 ? null : Rate(human),
            pii.Count == 0 ? null : Rate(pii),
            Rate(results.Select(r => r.SchemaValid)),
            Rate(results.Select(r => r.Attempts > 1)),
            recorder.Calls, recorder.Failures, recorder.Usage.InputTokens + recorder.Usage.CacheReadInputTokens + recorder.Usage.CacheCreationInputTokens,
            recorder.Usage.OutputTokens, recorder.CostUsd, elapsed.TotalSeconds, results);
    }

    public void PrintToConsole()
    {
        Console.WriteLine();
        foreach (var r in Results.Where(r => !r.CategoryOk || !r.PriorityWithinOne || !r.SchemaValid))
        {
            Console.WriteLine($"  ✖ {r.Id,-8} categoría {r.ActualCategory ?? "-",-16} (esperada {r.ExpectedCategory,-16}) prioridad {r.ActualPriority?.ToString() ?? "-",-7} (esperada {r.ExpectedPriority}) {r.Error}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Categoría            {CategoryAccuracy,8:P1}");
        Console.WriteLine($"  Prioridad exacta     {PriorityAccuracy,8:P1}");
        Console.WriteLine($"  Prioridad ±1 nivel   {PriorityWithinOneAccuracy,8:P1}");
        Console.WriteLine($"  Equipo               {TeamAccuracy,8:P1}");
        Console.WriteLine($"  requires_human       {(RequiresHumanAccuracy is { } h ? h.ToString("P1", CultureInfo.InvariantCulture) : "n/a"),8}");
        Console.WriteLine($"  JSON válido          {SchemaValidRate,8:P1}  (reparaciones: {RepairRate:P0})");
        Console.WriteLine($"  Llamadas / fallos    {Calls} / {Failures}   tokens in/out {InputTokens:N0}/{OutputTokens:N0}   costo ≈ ${CostUsd:0.0000}   {ElapsedSeconds:0.0}s");
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Evaluación de `{PromptKey}` v{PromptVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Modelo `{Model}` · effort `{Effort}` · proveedor `{Provider}` · {Cases} casos · {ElapsedSeconds:0.0}s · costo ≈ ${CostUsd:0.0000}");
        sb.AppendLine();
        sb.AppendLine("| Métrica | Valor |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Categoría | {CategoryAccuracy:P1} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Prioridad exacta | {PriorityAccuracy:P1} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Prioridad ±1 | {PriorityWithinOneAccuracy:P1} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Equipo | {TeamAccuracy:P1} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| requires_human | {(RequiresHumanAccuracy is { } h ? h.ToString("P1", CultureInfo.InvariantCulture) : "n/a")} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| PII detectada | {(PiiAccuracy is { } p ? p.ToString("P1", CultureInfo.InvariantCulture) : "n/a")} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| JSON válido | {SchemaValidRate:P1} (reparaciones {RepairRate:P0}) |");
        sb.AppendLine();
        sb.AppendLine("| Caso | Categoría | Prioridad | Equipo | Confianza | Error |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in Results)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Id} | {(r.CategoryOk ? "✅" : "❌")} {r.ActualCategory} | {(r.PriorityOk ? "✅" : r.PriorityWithinOne ? "≈" : "❌")} {r.ActualPriority} | {(r.TeamOk ? "✅" : "❌")} {r.ActualTeam} | {r.Confidence:0.00} | {r.Error} |");
        }

        return sb.ToString();
    }
}
