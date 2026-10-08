using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportAI.Api.IntegrationTests.Infrastructure;
using SupportAI.Contracts.Tickets;
using SupportAI.Infrastructure.BackgroundJobs;
using SupportAI.Infrastructure.Persistence;
using SupportAI.Infrastructure.Persistence.Outbox;

namespace SupportAI.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MultiTenancyTests(SupportAIApiFactory factory)
{
    [Fact]
    public async Task OtherTenantAdmin_CannotSeeOrReadAcmeTickets()
    {
        var acmeCustomer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var globexAdmin = await factory.CreateClientForAsync(DemoUsers.GlobexAdmin);
        var created = await acmeCustomer.CreateTicketAsync("Solo Acme");

        (await globexAdmin.GetAsync($"/api/v1/tickets/{created.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globexAdmin.ListTicketsAsync("?pageSize=100")).Items.ShouldNotContain(t => t.Id == created.Id);
    }

    [Fact]
    public async Task OtherTenantAdmin_SeesOnlyItsOwnKnowledgeBase()
    {
        var globexAdmin = await factory.CreateClientForAsync(DemoUsers.GlobexAdmin);

        var response = await globexAdmin.GetAsync("/api/v1/knowledge-articles");

        (await response.Content.ReadAsStringAsync()).ShouldBe("[]");
    }
}

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BackgroundJobsTests(SupportAIApiFactory factory)
{
    [Fact]
    public async Task CreatingTicket_EnqueuesOutboxMessage_ThatProcessorMarksProcessed()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var created = await customer.CreateTicketAsync("Outbox");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
            (await db.OutboxMessages.AnyAsync(m => m.Type == "TicketCreated" && m.Payload.Contains(created.Id.ToString()))).ShouldBeTrue();
        }

        var processor = factory.GetService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupportAIDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Type == "TicketCreated" && m.Payload.Contains(created.Id.ToString()));
            message.Status.ShouldBe(OutboxStatus.Processed, message.LastError);
        }
    }

    [Fact]
    public async Task SlaMonitor_AfterDueDate_MarksTicketAsBreached()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await agent.CreateTicketAsync("SLA");
        (await agent.PutJsonAsync($"/api/v1/tickets/{created.Id}/priority", new ChangePriorityRequest(Contracts.Common.TicketPriority.Urgent))).EnsureSuccessStatusCode();

        // Urgente: 30 min primera respuesta, 4 h resolución. El reloj compartido solo avanza (FakeTimeProvider);
        // la validación del JWT usa el mismo reloj, así que el resto de tests no se ve afectado.
        factory.Clock.Advance(TimeSpan.FromHours(5));
        (await factory.GetService<SlaMonitor>().CheckAsync(CancellationToken.None)).ShouldBeGreaterThan(0);

        agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var detail = await agent.GetTicketAsync(created.Id);
        detail.Summary.SlaBreached.ShouldBeTrue();
        detail.Activities.ShouldContain(a => a.Type == "SlaBreached");
    }
}

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthAndDocsTests(SupportAIApiFactory factory)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreHealthy(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Healthy");
    }

    [Fact]
    public async Task Responses_IncludeCorrelationAndSecurityHeaders()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");

        response.Headers.Contains("X-Correlation-Id").ShouldBeTrue();
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
    }
}
