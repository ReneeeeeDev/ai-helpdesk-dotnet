using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Application.Features.Ai.Replies;
using SupportAI.Application.Features.Ai.Triage;
using SupportAI.Infrastructure.Ai;
using SupportAI.Infrastructure.Ai.Fake;

namespace SupportAI.Application.Tests.Ai;

/// <summary>Contrato entre los prompts versionados (archivos), sus esquemas JSON y los records de C#.</summary>
public class PromptCatalogTests
{
    private static readonly IReadOnlyList<PromptFile> Prompts = PromptCatalog.LoadAll();

    public static TheoryData<string> PromptsWithSchema => new(Prompts.Where(p => p.JsonSchema is not null).Select(p => $"{p.Key}@{p.Version}"));

    [Fact]
    public void Catalog_ContainsEveryRequiredPromptKey()
    {
        var keys = Prompts.Select(p => p.Key).ToHashSet();

        foreach (var key in new[] { PromptKeys.Triage, PromptKeys.Reply, PromptKeys.Summary, PromptKeys.Copilot })
        {
            keys.ShouldContain(key);
        }

        Prompts.ShouldAllBe(p => p.Model == "claude-opus-5-5" && p.MaxTokens > 0);
    }

    [Theory]
    [MemberData(nameof(PromptsWithSchema))]
    public void Schema_IsStrictForStructuredOutputs(string id)
    {
        var prompt = Prompts.Single(p => $"{p.Key}@{p.Version}" == id);
        var schema = JsonNode.Parse(prompt.JsonSchema!)!.AsObject();

        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        var properties = schema["properties"]!.AsObject().Select(p => p.Key).OrderBy(k => k).ToList();
        var required = schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).OrderBy(k => k).ToList();
        required.ShouldBe(properties, "structured outputs exige que todas las propiedades sean required");

        // Palabras clave no soportadas por structured outputs: las restricciones numéricas/longitud van en el validador semántico.
        prompt.JsonSchema!.ShouldNotContain("\"minimum\"");
        prompt.JsonSchema!.ShouldNotContain("\"maxLength\"");
    }

    [Fact]
    public void UserTemplates_OnlyUseKnownPlaceholders()
    {
        var triage = Prompts.Single(p => p.Key == PromptKeys.Triage);

        Should.NotThrow(() => PromptRenderer.Render(triage.UserTemplate, new Dictionary<string, string>
        {
            ["categories"] = "- acceso",
            ["teams"] = "- Soporte: acceso",
            ["channel"] = "Web",
            ["created_at"] = "2026-10-07",
            ["subject"] = "s",
            ["description"] = "d",
        }));
    }

    [Theory]
    [InlineData("No puedo iniciar sesión, el código 2FA no funciona y es urgente", "acceso", "Soporte Técnico")]
    [InlineData("Me cobraron dos veces la suscripción, necesito el reembolso", "facturacion", "Facturación")]
    [InlineData("Payments webhook returns 401 since yesterday", "integraciones", "Soporte Técnico")]
    public async Task FakeTriage_ProducesSchemaValidOutputThatDeserializes(string description, string expectedCategory, string expectedTeam)
    {
        var prompt = Prompts.Single(p => p.Key == PromptKeys.Triage);
        var user = PromptRenderer.Render(prompt.UserTemplate, new Dictionary<string, string>
        {
            ["categories"] = "- acceso\n- facturacion\n- integraciones\n- consulta-general",
            ["teams"] = "- Soporte Técnico: acceso, integraciones\n- Facturación: facturacion",
            ["channel"] = "Web",
            ["created_at"] = "2026-10-07",
            ["subject"] = "Problema",
            ["description"] = PromptRenderer.Untrusted(description),
        });

        var response = await new FakeLlmClient(Options.Create(new LlmOptions { FakeLatency = TimeSpan.Zero })).CompleteAsync(new LlmRequest
        {
            Operation = "triage",
            Model = prompt.Model,
            Effort = prompt.Effort,
            MaxTokens = prompt.MaxTokens,
            SystemPrompt = prompt.SystemPrompt,
            Messages = [new LlmMessage(LlmRole.User, user)],
            JsonSchema = prompt.JsonSchema,
        }, CancellationToken.None);

        new JsonSchemaValidator().Validate(response.Text, prompt.JsonSchema!).ShouldBeEmpty();
        var output = JsonSerializer.Deserialize<TriageOutput>(response.Text, StructuredCompletionService.JsonOptions)!;
        output.Category.ShouldBe(expectedCategory);
        output.SuggestedTeam.ShouldBe(expectedTeam);
        output.Validate(["acceso", "facturacion", "integraciones", "consulta-general"], ["Soporte Técnico", "Facturación"]).ShouldBeEmpty();
    }

    [Fact]
    public void ReplyOutput_SampleMatchesSchema()
    {
        var prompt = Prompts.Single(p => p.Key == PromptKeys.Reply);
        var sample = """{"reply_markdown":"Hola","cited_article_ids":["a"],"confidence":0.8,"should_escalate":false,"follow_up_questions":[]}""";

        new JsonSchemaValidator().Validate(sample, prompt.JsonSchema!).ShouldBeEmpty();
        JsonSerializer.Deserialize<ReplyOutput>(sample, StructuredCompletionService.JsonOptions)!.CitedArticleIds.ShouldBe(["a"]);
    }

    [Fact]
    public void ConstrainSchema_InjectsTenantCategoriesAndTeamsAsEnums()
    {
        var prompt = Prompts.Single(p => p.Key == PromptKeys.Triage);

        var schema = TicketTriageService.ConstrainSchema(JsonNode.Parse(prompt.JsonSchema!)!.AsObject(), ["acceso", "bug"], ["Soporte"]);

        schema["properties"]!["category"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["acceso", "bug"]);
        schema["properties"]!["suggested_team"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["Soporte", ""]);
        new JsonSchemaValidator().Validate("""{"category":"otra"}""", schema.ToJsonString()).ShouldNotBeEmpty();
    }
}
