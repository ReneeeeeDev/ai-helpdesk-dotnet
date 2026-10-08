using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Domain.Ai;
using SupportAI.Infrastructure.Ai;

namespace SupportAI.Application.Tests.Ai;

public class StructuredCompletionServiceTests
{
    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["name","score"],
         "properties":{"name":{"type":"string"},"score":{"type":"number"}}}
        """;

    private static readonly PromptDefinition Prompt = new("test.prompt", 3, "system", "user", Schema, "claude-opus-5-5", "low", 1000);
    private static readonly LlmCallContext Context = new("test", null, "test.prompt", 3);

    private readonly ILlmClient _llm = Substitute.For<ILlmClient>();
    private readonly ILlmCallRecorder _recorder = Substitute.For<ILlmCallRecorder>();

    public sealed record Output(string Name, decimal Score);

    private StructuredCompletionService CreateService() =>
        new(_llm, new JsonSchemaValidator(), _recorder, NullLogger<StructuredCompletionService>.Instance);

    private static LlmResponse Response(string text, string stop = LlmStopReasons.EndTurn) =>
        new(text, stop, new LlmUsage(100, 20), "claude-opus-5-5", TimeSpan.FromMilliseconds(5));

    private static IEnumerable<string> ScoreInRange(Output o)
    {
        if (o.Score is < 0 or > 1)
        {
            yield return "score debe estar entre 0 y 1";
        }
    }

    [Fact]
    public async Task ValidOutput_ReturnsTypedValueAndRecordsCall()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(Response("""{"name":"ok","score":0.7}"""));

        var result = await CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.Value!.Name.ShouldBe("ok");
        result.Attempts.ShouldBe(1);
        await _recorder.Received(1).RecordSuccessAsync(Context, Arg.Any<LlmResponse>(), 1, Arg.Any<CancellationToken>());
        await _llm.Received(1).CompleteAsync(Arg.Is<LlmRequest>(r => r.JsonSchema == Schema && r.Effort == "low"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SemanticError_TriggersRepairWithFeedback_ThenSucceeds()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(Response("""{"name":"x","score":7}"""), Response("""{"name":"x","score":0.7}"""));

        var result = await CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        result.Attempts.ShouldBe(2);
        result.Usage.InputTokens.ShouldBe(200);
        // El segundo request incluye la salida previa (assistant) y los errores (user), sin prefill.
        await _llm.Received(1).CompleteAsync(
            Arg.Is<LlmRequest>(r => r.Messages.Count == 3 && r.Messages[1].Role == LlmRole.Assistant && r.Messages[2].Content.Contains("score debe estar")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SchemaViolationTwice_ReturnsInvalidWithErrors()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(Response("""{"name":"x"}"""));

        var result = await CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        result.RawJson.ShouldBe("""{"name":"x"}""");
    }

    [Fact]
    public async Task MaxTokens_RetriesWithDoubleBudget()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns(Response("""{"name":""", LlmStopReasons.MaxTokens), Response("""{"name":"ok","score":0.5}"""));

        var result = await CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None);

        result.IsValid.ShouldBeTrue();
        await _llm.Received(1).CompleteAsync(Arg.Is<LlmRequest>(r => r.MaxTokens == 2000), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refusal_ThrowsRefusedLlmException()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>()).Returns(Response(string.Empty, LlmStopReasons.Refusal));

        var ex = await Should.ThrowAsync<LlmException>(() => CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Refused);
        ex.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderError_IsRecordedAsFailureAndRethrown()
    {
        _llm.CompleteAsync(Arg.Any<LlmRequest>(), Arg.Any<CancellationToken>())
            .Returns<LlmResponse>(_ => throw new LlmException(LlmErrorKind.Overloaded, "529"));

        await Should.ThrowAsync<LlmException>(() => CreateService().CompleteAsync<Output>(Prompt, "hola", Context, ScoreInRange, CancellationToken.None));

        await _recorder.Received(1).RecordFailureAsync(Context, "claude-opus-5-5", Arg.Any<LlmException>(), Arg.Any<TimeSpan>(), 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Pricing_EstimatesCostIncludingCache()
    {
        var pricing = new LlmPricingOptions();
        pricing.Models["claude-opus-5-5"] = new ModelPricing { InputPerMTok = 4m, OutputPerMTok = 20m, CacheWritePerMTok = 5m, CacheReadPerMTok = 0.2m };

        var cost = pricing.Estimate("claude-opus-5-5", new LlmUsage(1_000_000, 100_000, 0, 1_000_000));

        cost.ShouldBe(4m + 2m + 0.2m);
        pricing.Estimate("modelo-desconocido", new LlmUsage(10, 10)).ShouldBe(0m);
    }
}
