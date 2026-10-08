using System.Net;
using System.Text;
using SupportAI.Api.IntegrationTests.Infrastructure;
using SupportAI.Contracts.Ai;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Organization;
using SupportAI.Infrastructure.BackgroundJobs;

namespace SupportAI.Api.IntegrationTests;

/// <summary>Flujos de IA de punta a punta con el proveedor Fake (sin llamadas reales ni costo).</summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AiTests(SupportAIApiFactory factory)
{
    private async Task DrainOutboxAsync()
    {
        var processor = factory.GetService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task NewTicket_IsTriagedAutomatically_ViaOutbox()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync("No puedo iniciar sesión", "El código 2FA no funciona y no puedo entrar, es urgente.");

        await DrainOutboxAsync();

        var detail = await agent.GetTicketAsync(created.Id);
        detail.LatestTriage.ShouldNotBeNull();
        detail.LatestTriage.Status.ShouldBe(TriageResultStatus.AutoApplied);
        detail.LatestTriage.PromptKey.ShouldBe("ticket.triage");
        detail.Summary.Status.ShouldBe(TicketStatus.Triaged);
        detail.Summary.Category.ShouldBe("acceso");
        detail.Summary.TeamName.ShouldBe("Soporte Técnico");
        detail.Summary.Priority.ShouldBe(TicketPriority.Urgent);
        detail.AiSummary.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LowConfidenceTriage_IsSuggested_AndAgentCanOverride()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync("Consulta", "Tengo una duda sobre algo.");
        await DrainOutboxAsync();

        var triage = (await agent.GetTicketAsync(created.Id)).LatestTriage!;
        triage.Status.ShouldBe(TriageResultStatus.Suggested);
        (await agent.GetTicketAsync(created.Id)).Summary.NeedsHumanReview.ShouldBeTrue();

        var response = await agent.PostJsonAsync($"/api/v1/tickets/{created.Id}/ai/triage/{triage.Id}/override",
            new OverrideTriageRequest("cuenta", TicketPriority.High, null, "Es sobre su cuenta"));
        var overridden = await response.ReadAsync<TriageResultDto>();

        overridden.Status.ShouldBe(TriageResultStatus.Overridden);
        var detail = await agent.GetTicketAsync(created.Id);
        detail.Summary.Category.ShouldBe("cuenta");
        detail.Summary.Priority.ShouldBe(TicketPriority.High);
        detail.Summary.NeedsHumanReview.ShouldBeFalse();
    }

    [Fact]
    public async Task Retriage_ByAgent_CreatesNewResult_AndAcceptAppliesIt()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync("Duda", "Una pregunta general.");
        await DrainOutboxAsync();

        var result = await (await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/triage", null)).ReadAsync<TriageResultDto>();
        var history = await (await agent.GetAsync($"/api/v1/tickets/{created.Id}/ai/triage")).ReadAsync<List<TriageResultDto>>();
        var accepted = await (await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/triage/{result.Id}/accept", null)).ReadAsync<TriageResultDto>();

        history.Count.ShouldBe(2);
        accepted.Status.ShouldBe(TriageResultStatus.Accepted);
        (await agent.GetTicketAsync(created.Id)).Summary.Status.ShouldBe(TicketStatus.Triaged);
    }

    [Fact]
    public async Task SuggestReply_CitesKnowledgeBaseArticles()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync("Cobro duplicado", "Me cobraron dos veces la suscripción y necesito un reembolso.");

        var reply = await (await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/reply-suggestions", null)).ReadAsync<SuggestedReplyDto>();

        reply.Body.ShouldContain("Diego Soporte"); // firma del agente que la pidió
        reply.Citations.ShouldNotBeEmpty();
        reply.Citations[0].Title.ShouldContain("reembolsos", Case.Insensitive);
        (await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/reply-suggestions/{reply.Id}/used", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Customer_CannotUseAiEndpoints()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var created = await customer.CreateTicketAsync();

        (await customer.PostAsync($"/api/v1/tickets/{created.Id}/ai/reply-suggestions", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await customer.GetAsync($"/api/v1/tickets/{created.Id}/ai/summary/stream")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SummaryStream_EmitsServerSentEvents()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync("Dashboard lento", "El dashboard tarda 30 segundos en cargar.");

        var response = await agent.GetAsync($"/api/v1/tickets/{created.Id}/ai/summary/stream", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        body.ShouldContain("event: delta");
        body.ShouldContain("Problema");
        body.ShouldContain("event: end");
    }

    [Fact]
    public async Task SummaryStream_UnknownTicket_Returns404ProblemBeforeStreaming()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);

        var response = await agent.GetAsync($"/api/v1/tickets/{Guid.NewGuid()}/ai/summary/stream");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Copilot_UsesToolsAndStreamsAnswer()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);

        var response = await agent.PostAsync("/api/v1/ai/copilot", new StringContent(
            """{"question":"¿Cómo se resuelven los errores 401 en los webhooks?"}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldContain("event: tool");
        body.ShouldContain("search_knowledge_base");
        body.ShouldContain("event: text");
        body.ShouldContain("Webhooks");
        body.ShouldContain("event: done");
    }

    [Fact]
    public async Task UsageReport_ShowsCallsAndPromptAccuracy()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        await customer.CreateTicketAsync("Uso de IA", "No puedo iniciar sesión con 2FA.");
        await DrainOutboxAsync();
        var admin = await factory.CreateClientForAsync(DemoUsers.Admin);

        var report = await (await admin.GetAsync("/api/v1/ai/usage")).ReadAsync<AiUsageReportDto>();

        report.Days.ShouldContain(d => d.Operation == "triage" && d.Calls > 0);
        report.TokensThisMonth.ShouldBeGreaterThan(0);
        report.PromptAccuracy.ShouldContain(a => a.PromptKey == "ticket.triage" && a.PromptVersion == 1);
    }

    [Fact]
    public async Task Prompts_AreSyncedFromRepository_OneActivePerKey()
    {
        var admin = await factory.CreateClientForAsync(DemoUsers.Admin);

        var prompts = await (await admin.GetAsync("/api/v1/ai/prompts")).ReadAsync<List<PromptTemplateDto>>();

        prompts.Where(p => p.IsActive).Select(p => p.Key).ShouldBe(["copilot.agent", "ticket.reply", "ticket.summary", "ticket.triage"], ignoreOrder: true);
        (await admin.PostAsync("/api/v1/ai/prompts/ticket.triage/versions/99/activate", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync("/api/v1/ai/prompts/ticket.triage/versions/1/activate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExhaustedTokenBudget_Returns429()
    {
        var admin = await factory.CreateClientForAsync(DemoUsers.Admin);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var settings = await (await admin.GetAsync("/api/v1/settings")).ReadAsync<TenantSettingsDto>();
        var created = await agent.CreateTicketAsync("Presupuesto", "Prueba de presupuesto agotado.");
        await (await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/reply-suggestions", null)).ReadAsync<SuggestedReplyDto>();

        try
        {
            (await admin.PutJsonAsync("/api/v1/settings", new UpdateTenantSettingsRequest(settings.Categories, settings.AutoTriageEnabled, settings.AutoApplyThreshold, 1)))
                .EnsureSuccessStatusCode();

            var response = await agent.PostAsync($"/api/v1/tickets/{created.Id}/ai/reply-suggestions", null);

            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await response.ProblemCodeAsync()).ShouldBe("quota_exceeded");
            response.Headers.RetryAfter.ShouldNotBeNull();
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings", new UpdateTenantSettingsRequest(settings.Categories, settings.AutoTriageEnabled, settings.AutoApplyThreshold, settings.MonthlyTokenBudget));
        }
    }
}
