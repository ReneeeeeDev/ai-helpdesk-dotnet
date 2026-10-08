using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Application.Features.Ai.Triage;
using SupportAI.Domain.Ai;
using SupportAI.Infrastructure.Ai;
using SupportAI.Infrastructure.Ai.Claude;
using SupportAI.Infrastructure.Ai.Fake;
using SupportAI.PromptEvals;

// ─────────────────────────────────────────────────────────────────────────────
// Evaluación offline del prompt de triage contra un dataset dorado.
//
//   dotnet run --project tests/SupportAI.PromptEvals -- --provider fake
//   dotnet run --project tests/SupportAI.PromptEvals -- --provider claude --version 2 --min-category-accuracy 0.85
//
// Con --provider claude se hacen llamadas REALES (cuesta dinero). La API key se lee de
// ANTHROPIC_API_KEY o de user-secrets (Anthropic:ApiKey / Llm:ApiKey).
// ─────────────────────────────────────────────────────────────────────────────

var options = EvalOptions.Parse(args);
var configuration = new ConfigurationBuilder().AddUserSecrets<EvalOptions>(optional: true).AddEnvironmentVariables().Build();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

var prompts = PromptCatalog.LoadAll().Where(p => p.Key == PromptKeys.Triage).ToList();
var promptFile = options.Version is { } v
    ? prompts.SingleOrDefault(p => p.Version == v) ?? throw new InvalidOperationException($"No existe {PromptKeys.Triage} v{v}.")
    : prompts.MaxBy(p => p.Version)!;
var prompt = new PromptDefinition(promptFile.Key, promptFile.Version, promptFile.SystemPrompt, promptFile.UserTemplate,
    promptFile.JsonSchema, options.Model ?? promptFile.Model, promptFile.Effort, promptFile.MaxTokens);

var llmOptions = Options.Create(new LlmOptions { FakeLatency = TimeSpan.Zero, ServerSideFallback = "default" });
ILlmClient llm = options.Provider switch
{
    "claude" => new ClaudeLlmClient(CreateAnthropicClient(configuration), llmOptions, loggerFactory.CreateLogger<ClaudeLlmClient>()),
    "fake" => new FakeLlmClient(llmOptions),
    _ => throw new ArgumentException($"Proveedor desconocido: {options.Provider} (usa fake | claude)."),
};

var pricing = new LlmPricingOptions();
pricing.Models["claude-opus-5-5"] = new ModelPricing { InputPerMTok = 4m, OutputPerMTok = 20m, CacheWritePerMTok = 5m, CacheReadPerMTok = 0.20m };
pricing.Models["claude-sonnet-5-5"] = new ModelPricing { InputPerMTok = 2m, OutputPerMTok = 10m, CacheWritePerMTok = 2.5m, CacheReadPerMTok = 0.20m };
var recorder = new InMemoryRecorder(pricing);
var structured = new StructuredCompletionService(llm, new JsonSchemaValidator(), recorder, loggerFactory.CreateLogger<StructuredCompletionService>());
var redactor = new PiiRedactor();

var cases = (await File.ReadAllLinesAsync(options.Dataset))
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonSerializer.Deserialize<GoldenCase>(l, EvalJson.Options)!)
    .Take(options.MaxCases)
    .ToList();

Console.WriteLine($"Evaluando {PromptKeys.Triage} v{prompt.Version} ({prompt.Model}, effort={prompt.Effort}) con {cases.Count} casos · proveedor {llm.ProviderName}");

var gate = new SemaphoreSlim(options.Concurrency);
var stopwatch = Stopwatch.StartNew();
var results = await Task.WhenAll(cases.Select(async c =>
{
    await gate.WaitAsync();
    try
    {
        return await EvaluateAsync(c);
    }
    finally
    {
        gate.Release();
    }
}));
stopwatch.Stop();

var report = EvalReport.Create(prompt, llm.ProviderName, results, recorder, stopwatch.Elapsed);
report.PrintToConsole();
Directory.CreateDirectory(options.OutputDirectory);
var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var baseName = Path.Combine(options.OutputDirectory, $"triage-v{prompt.Version}-{llm.ProviderName}-{stamp}");
await File.WriteAllTextAsync(baseName + ".json", JsonSerializer.Serialize(report, EvalJson.Indented));
await File.WriteAllTextAsync(baseName + ".md", report.ToMarkdown(), Encoding.UTF8);
Console.WriteLine($"Reporte: {baseName}.md");

if (report.CategoryAccuracy < options.MinCategoryAccuracy || report.SchemaValidRate < 1.0)
{
    Console.Error.WriteLine($"✖ Por debajo del umbral: categoría {report.CategoryAccuracy:P1} (mínimo {options.MinCategoryAccuracy:P0}), JSON válido {report.SchemaValidRate:P0}.");
    return 1;
}

Console.WriteLine("✔ Umbrales cumplidos.");
return 0;

async Task<CaseResult> EvaluateAsync(GoldenCase c)
{
    var subject = redactor.Redact(c.Subject);
    var description = redactor.Redact(c.Description);
    var user = TicketTriageService.BuildUserMessage(prompt, EvalTenant.Categories, EvalTenant.Teams, c.Channel ?? "Web",
        DateTimeOffset.UtcNow, subject.Text, description.Text);
    var teamNames = EvalTenant.Teams.Select(t => t.Name).ToList();

    try
    {
        var result = await structured.CompleteAsync<TriageOutput>(
            prompt, user, new LlmCallContext("triage", null, prompt.Key, prompt.Version),
            o => o.Validate(EvalTenant.Categories, teamNames), CancellationToken.None,
            schema => TicketTriageService.ConstrainSchema(schema, EvalTenant.Categories, teamNames));

        return CaseResult.From(c, result.Value, result.IsValid, result.Attempts, string.Join(" | ", result.Errors), subject.FoundAny || description.FoundAny);
    }
    catch (LlmException ex)
    {
        return CaseResult.Failed(c, $"{ex.Kind}: {ex.Message}");
    }
}

static AnthropicClient CreateAnthropicClient(IConfiguration configuration)
{
    var key = configuration["Anthropic:ApiKey"] ?? configuration["Llm:ApiKey"];
    return string.IsNullOrWhiteSpace(key) ? new AnthropicClient() : new AnthropicClient { ApiKey = key };
}
