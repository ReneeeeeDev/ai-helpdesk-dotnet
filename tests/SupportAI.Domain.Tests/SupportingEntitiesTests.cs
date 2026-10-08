using SupportAI.Domain.Ai;
using SupportAI.Domain.Common;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;

namespace SupportAI.Domain.Tests;

public class TenantTests
{
    [Fact]
    public void Create_NormalizesCategories()
    {
        var tenant = Tenant.Create("Acme", "acme", ["Facturación", " facturación ", "Acceso", ""]);

        tenant.Categories.ShouldBe(["facturación", "acceso"]);
        tenant.HasCategory("ACCESO").ShouldBeTrue();
        tenant.HasCategory("otra").ShouldBeFalse();
    }

    [Fact]
    public void Create_InvalidSlug_Throws() =>
        Should.Throw<DomainException>(() => Tenant.Create("Acme", "acme corp!", ["x"])).Code.ShouldBe("tenant_slug_invalid");

    [Fact]
    public void Create_NoCategories_Throws() =>
        Should.Throw<DomainException>(() => Tenant.Create("Acme", "acme", [])).Code.ShouldBe("tenant_categories_required");

    [Theory]
    [InlineData(0.85, false, true)]
    [InlineData(0.79, false, false)]
    [InlineData(0.99, true, false)]
    public void AiSettings_ShouldAutoApply_RespectsThresholdAndHumanFlag(decimal confidence, bool requiresHuman, bool expected) =>
        TenantAiSettings.Default.ShouldAutoApply(confidence, requiresHuman).ShouldBe(expected);

    [Fact]
    public void AiSettings_InvalidThreshold_Throws() =>
        Should.Throw<DomainException>(() => new TenantAiSettings(true, 1.5m, 0));
}

public class SlaPolicyTests
{
    [Fact]
    public void SetTargets_MissingPriority_Throws() =>
        Should.Throw<DomainException>(() => SlaPolicy.Create(Guid.CreateVersion7(), "Incompleta", [
            new SlaTarget(TicketPriority.Low, TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
        ])).Code.ShouldBe("sla_incomplete");

    [Fact]
    public void SlaTarget_ResolutionShorterThanFirstResponse_Throws() =>
        Should.Throw<DomainException>(() => new SlaTarget(TicketPriority.Low, TimeSpan.FromHours(5), TimeSpan.FromHours(1)));

    [Fact]
    public void CreateStandard_CoversAllPriorities()
    {
        var policy = SlaPolicy.CreateStandard(Guid.CreateVersion7());

        foreach (var priority in Enum.GetValues<TicketPriority>())
        {
            policy.TargetFor(priority).Priority.ShouldBe(priority);
        }
    }
}

public class UserTests
{
    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("@dominio.com")]
    [InlineData("a@b@c")]
    public void Create_InvalidEmail_Throws(string email) =>
        Should.Throw<DomainException>(() => User.Create(Guid.CreateVersion7(), email, "Ana", UserRole.Agent)).Code.ShouldBe("user_email_invalid");

    [Fact]
    public void Customer_CannotJoinTeam()
    {
        var user = User.Create(Guid.CreateVersion7(), "cliente@acme.com", "Cliente", UserRole.Customer);

        Should.Throw<DomainException>(() => user.AssignToTeam(Guid.CreateVersion7())).Code.ShouldBe("user_team_not_allowed");
    }

    [Fact]
    public void Create_NormalizesEmail() =>
        User.Create(Guid.CreateVersion7(), "  Ana@Acme.COM ", "Ana", UserRole.Admin).Email.ShouldBe("ana@acme.com");
}

public class AiTriageResultTests
{
    private static AiTriageResult NewResult() => AiTriageResult.Create(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "ticket.triage", 1, "claude-opus-5-5",
        new TriageDecision("acceso", TicketPriority.High, Sentiment.Negative, "es", null, ["login"], "Resumen"),
        suggestedTeam: "Soporte", containsPii: false, requiresHuman: false, confidence: 1.7m, reasoning: "r", rawJson: "{}", llmCallId: null,
        now: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Create_ClampsConfidence() => NewResult().Confidence.ShouldBe(1m);

    [Fact]
    public void Accept_ThenOverride_Throws()
    {
        var result = NewResult();
        result.Accept(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);

        Should.Throw<DomainException>(() => result.Override(Guid.CreateVersion7(), "no", DateTimeOffset.UnixEpoch)).Code.ShouldBe("triage_already_reviewed");
    }

    [Fact]
    public void AutoApplied_CanStillBeOverridden()
    {
        var result = NewResult();
        result.MarkAutoApplied();

        result.Override(Guid.CreateVersion7(), "La prioridad era Low", DateTimeOffset.UnixEpoch);

        result.Status.ShouldBe(TriageResultStatus.Overridden);
        result.ReviewNotes.ShouldBe("La prioridad era Low");
    }

    [Fact]
    public void ToDecision_RoundTripsFields()
    {
        var decision = NewResult().ToDecision();

        decision.Category.ShouldBe("acceso");
        decision.Priority.ShouldBe(TicketPriority.High);
        decision.Tags.ShouldBe(["login"]);
    }
}

public class PromptTemplateTests
{
    [Fact]
    public void ComputeHash_IsDeterministicAndSensitiveToContent()
    {
        var a = PromptTemplate.ComputeHash("sys", "user", "{}", "claude-opus-5-5", "low", 1000);
        var b = PromptTemplate.ComputeHash("sys", "user", "{}", "claude-opus-5-5", "low", 1000);
        var c = PromptTemplate.ComputeHash("sys ", "user", "{}", "claude-opus-5-5", "low", 1000);

        a.ShouldBe(b);
        a.ShouldNotBe(c);
    }

    [Fact]
    public void Create_InvalidEffort_Throws() =>
        Should.Throw<DomainException>(() => PromptTemplate.Create("k", 1, "d", "s", "u", null, "m", "turbo", 100, DateTimeOffset.UnixEpoch))
            .Code.ShouldBe("prompt_invalid_effort");
}

public class LlmUsageTests
{
    [Fact]
    public void Addition_SumsAllCounters()
    {
        var total = new LlmUsage(10, 5, 2, 100) + new LlmUsage(1, 1, 0, 50);

        total.ShouldBe(new LlmUsage(11, 6, 2, 150));
        total.Total.ShouldBe(169);
    }
}
