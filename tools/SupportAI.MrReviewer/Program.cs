using System.Globalization;
using System.Text;
using Anthropic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Domain.Ai;
using SupportAI.Infrastructure.Ai;
using SupportAI.Infrastructure.Ai.Claude;
using SupportAI.Infrastructure.Ai.Fake;
using SupportAI.MrReviewer;

// ─────────────────────────────────────────────────────────────────────────────
// Revisión automática de un MR con Claude. Apoya al revisor humano; no lo reemplaza.
//
//   git diff origin/main...HEAD > mr.diff
//   dotnet run --project tools/SupportAI.MrReviewer -- --diff mr.diff [--provider fake|claude] [--post]
// ─────────────────────────────────────────────────────────────────────────────

const int MaxDiffChars = 150_000;

string? diffPath = null;
var provider = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "claude" : "fake";
var post = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--diff": diffPath = args[++i]; break;
        case "--provider": provider = args[++i].ToLowerInvariant(); break;
        case "--post": post = true; break;
        default: throw new ArgumentException($"Argumento desconocido: {args[i]}");
    }
}

if (diffPath is null || !File.Exists(diffPath))
{
    Console.Error.WriteLine("Uso: --diff <archivo.diff> [--provider fake|claude] [--post]");
    return 2;
}

var diff = await File.ReadAllTextAsync(diffPath);
if (string.IsNullOrWhiteSpace(diff))
{
    Console.WriteLine("Diff vacío: nada que revisar.");
    return 0;
}

var truncated = diff.Length > MaxDiffChars;
if (truncated)
{
    diff = diff[..MaxDiffChars];
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
var llmOptions = Options.Create(new LlmOptions { FakeLatency = TimeSpan.Zero });
ILlmClient llm = provider == "claude"
    ? new ClaudeLlmClient(new AnthropicClient(), llmOptions, loggerFactory.CreateLogger<ClaudeLlmClient>())
    : new FakeLlmClient(llmOptions);

var structured = new StructuredCompletionService(llm, new JsonSchemaValidator(), new NoopRecorder(), loggerFactory.CreateLogger<StructuredCompletionService>());
var prompt = ReviewPrompt.Definition;
var user = PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
{
    ["title"] = PromptRenderer.Untrusted(Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_TITLE") ?? "(local)"),
    ["source"] = Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_SOURCE_BRANCH_NAME") ?? "HEAD",
    ["target"] = Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_TARGET_BRANCH_NAME") ?? "main",
    // El diff es contenido no confiable: se escapa para que no pueda cerrar la etiqueta <diff>.
    ["diff"] = PromptRenderer.Untrusted(diff),
});

var result = await structured.CompleteAsync<ReviewOutput>(prompt, user, new LlmCallContext("review", null, prompt.Key, prompt.Version),
    o => o.Findings.Where(f => f.Line < 0).Select(f => $"line inválida en {f.File}"), CancellationToken.None);

if (!result.IsValid || result.Value is null)
{
    Console.Error.WriteLine($"La revisión no produjo una salida válida: {string.Join(" | ", result.Errors)}");
    return 1;
}

var markdown = Render(result.Value, llm.ProviderName, truncated);
await File.WriteAllTextAsync("ai-review.md", markdown, Encoding.UTF8);
Console.WriteLine(markdown);

if (post)
{
    var gitlab = GitLabNotes.FromEnvironment();
    if (gitlab is null)
    {
        Console.Error.WriteLine("--post requiere CI_API_V4_URL, CI_PROJECT_ID, CI_MERGE_REQUEST_IID y GITLAB_REVIEW_TOKEN.");
        return 1;
    }

    await gitlab.UpsertAsync(markdown, CancellationToken.None);
    Console.WriteLine("Revisión publicada en el MR.");
}

// La revisión es informativa: no hace fallar el pipeline salvo error técnico.
return 0;

static string Render(ReviewOutput review, string provider, bool truncated)
{
    var sb = new StringBuilder();
    var riskIcon = review.Risk switch { ReviewRisk.High => "🔴", ReviewRisk.Medium => "🟠", _ => "🟢" };
    sb.AppendLine(CultureInfo.InvariantCulture, $"## 🤖 Revisión automática ({provider})");
    sb.AppendLine();
    sb.AppendLine(CultureInfo.InvariantCulture, $"**Riesgo:** {riskIcon} {review.Risk.ToString().ToLowerInvariant()} · **Hallazgos:** {review.Findings.Count}");
    sb.AppendLine();
    sb.AppendLine(review.Summary);
    if (truncated)
    {
        sb.AppendLine();
        sb.AppendLine("> ⚠️ El diff era demasiado grande y se truncó: la revisión puede estar incompleta.");
    }

    foreach (var group in review.Findings.OrderBy(f => f.Severity).GroupBy(f => f.Severity))
    {
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {group.Key} ({group.Count()})");
        foreach (var f in group)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **{f.Title}** — `{f.File}:{f.Line}` · _{f.Category.ToString().ToLowerInvariant()}_");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {f.Explanation}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  💡 {f.Suggestion}");
        }
    }

    if (review.TestsRecommended.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("### Tests recomendados");
        foreach (var t in review.TestsRecommended)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {t}");
        }
    }

    sb.AppendLine();
    sb.AppendLine("_Generado por `tools/SupportAI.MrReviewer`. Es un apoyo: la aprobación sigue siendo de un revisor humano._");
    return sb.ToString();
}

internal sealed class NoopRecorder : ILlmCallRecorder
{
    public Task<Guid> RecordSuccessAsync(LlmCallContext ctx, LlmResponse response, int attempt, CancellationToken ct) => Task.FromResult(Guid.CreateVersion7());

    public Task RecordFailureAsync(LlmCallContext ctx, string model, Exception error, TimeSpan latency, int attempt, CancellationToken ct) => Task.CompletedTask;
}
