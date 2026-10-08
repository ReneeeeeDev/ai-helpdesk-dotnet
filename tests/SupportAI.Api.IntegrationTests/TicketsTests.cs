using System.Net;
using System.Net.Http.Headers;
using SupportAI.Api.IntegrationTests.Infrastructure;
using SupportAI.Application.Features.Tickets;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Tickets;

namespace SupportAI.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TicketsTests(SupportAIApiFactory factory)
{
    [Fact]
    public async Task Customer_CreatesTicket_GetsNumberAndCanReadIt()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);

        var created = await customer.CreateTicketAsync("La app no sincroniza", "Desde hoy no sincroniza el calendario.");
        var detail = await customer.GetTicketAsync(created.Id);

        created.Number.ShouldBeGreaterThanOrEqualTo(1000);
        detail.Summary.Status.ShouldBe(TicketStatus.New);
        detail.Summary.ResolutionDueAt.ShouldNotBeNull();
        detail.Activities.ShouldBeEmpty(); // el historial es interno
        detail.AllowedTransitions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Customer_CannotSeeAnotherCustomersTicket()
    {
        var owner = await factory.CreateClientForAsync(DemoUsers.Customer);
        var other = await factory.CreateClientForAsync(DemoUsers.OtherCustomer);
        var created = await owner.CreateTicketAsync();

        (await other.GetAsync($"/api/v1/tickets/{created.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.ListTicketsAsync("?pageSize=100")).Items.ShouldNotContain(t => t.Id == created.Id);
    }

    [Fact]
    public async Task AgentReply_OpensTicket_AndInternalNoteIsHiddenFromCustomer()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await customer.CreateTicketAsync();

        (await agent.PostJsonAsync($"/api/v1/tickets/{created.Id}/messages", new AddMessageRequest("Nota: revisar logs", IsInternal: true))).EnsureSuccessStatusCode();
        (await agent.PostJsonAsync($"/api/v1/tickets/{created.Id}/messages", new AddMessageRequest("Hola, ya lo estamos revisando."))).EnsureSuccessStatusCode();

        var asAgent = await agent.GetTicketAsync(created.Id);
        var asCustomer = await customer.GetTicketAsync(created.Id);

        asAgent.Summary.Status.ShouldBe(TicketStatus.Open);
        asAgent.FirstRespondedAt.ShouldNotBeNull();
        asAgent.Messages.Count.ShouldBe(2);
        asCustomer.Messages.ShouldHaveSingleItem().IsInternal.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangeStatus_InvalidTransition_Returns422WithCode()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await agent.CreateTicketAsync();

        var response = await agent.PutJsonAsync($"/api/v1/tickets/{created.Id}/status", new ChangeStatusRequest(TicketStatus.Resolved));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ProblemCodeAsync()).ShouldBe("ticket_invalid_transition");
    }

    [Fact]
    public async Task ChangeStatus_StaleIfMatch_Returns409()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var created = await agent.CreateTicketAsync();
        var original = await agent.GetTicketAsync(created.Id);
        (await agent.PutJsonAsync($"/api/v1/tickets/{created.Id}/status", new ChangeStatusRequest(TicketStatus.Open))).EnsureSuccessStatusCode();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tickets/{created.Id}/priority")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new ChangePriorityRequest(TicketPriority.High), options: SupportAIApiFactory.Json),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{original.RowVersion}\""));
        var response = await agent.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ProblemCodeAsync()).ShouldBe("ticket_version_mismatch");
    }

    [Fact]
    public async Task Customer_CannotChangeStatus_Returns403()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var created = await customer.CreateTicketAsync();

        (await customer.PutJsonAsync($"/api/v1/tickets/{created.Id}/status", new ChangeStatusRequest(TicketStatus.Closed)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_WithSameIdempotencyKey_ReplaysOriginalResponse()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var key = Guid.NewGuid().ToString("N");
        var body = new CreateTicketRequest("Idempotente", "Cuerpo");

        async Task<HttpResponseMessage> Send(CreateTicketRequest payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tickets")
            {
                Content = System.Net.Http.Json.JsonContent.Create(payload, options: SupportAIApiFactory.Json),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await customer.SendAsync(request);
        }

        var first = await (await Send(body)).ReadAsync<TicketCreatedResult>();
        var replay = await Send(body);
        var conflict = await Send(body with { Subject = "Otro asunto" });

        replay.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await replay.ReadAsync<TicketCreatedResult>()).Id.ShouldBe(first.Id);
        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Validation_ReturnsCamelCaseErrors()
    {
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);

        var response = await customer.PostJsonAsync("/api/v1/tickets", new CreateTicketRequest("", ""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"subject\"");
        body.ShouldContain("\"description\"");
    }

    [Fact]
    public async Task List_KeysetPagination_ReturnsEveryTicketExactlyOnce()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        for (var i = 0; i < 5; i++)
        {
            await agent.CreateTicketAsync($"Paginación {i}");
        }

        var all = await agent.ListTicketsAsync("?pageSize=100&includeClosed=true");
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await agent.ListTicketsAsync($"?pageSize=3&includeClosed=true{(cursor is null ? "" : $"&cursor={cursor}")}");
            seen.AddRange(page.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.ShouldBe(all.Items.Select(t => t.Id).ToList());
        seen.Distinct().Count().ShouldBe(all.TotalCount);
    }

    [Fact]
    public async Task List_SortByPriority_OrdersBySeverityDescending()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var low = await agent.CreateTicketAsync("Prioridad baja");
        var urgent = await agent.CreateTicketAsync("Prioridad urgente");
        (await agent.PutJsonAsync($"/api/v1/tickets/{low.Id}/priority", new ChangePriorityRequest(TicketPriority.Low))).EnsureSuccessStatusCode();
        (await agent.PutJsonAsync($"/api/v1/tickets/{urgent.Id}/priority", new ChangePriorityRequest(TicketPriority.Urgent))).EnsureSuccessStatusCode();

        var page = await agent.ListTicketsAsync("?sort=Priority&pageSize=100");

        var priorities = page.Items.Select(t => (int)t.Priority).ToList();
        priorities.ShouldBe(priorities.OrderByDescending(p => p).ToList());
        page.Items[0].Priority.ShouldBe(TicketPriority.Urgent);
    }

    [Fact]
    public async Task Assign_ToCustomer_ReturnsValidationError()
    {
        var agent = await factory.CreateClientForAsync(DemoUsers.Agent);
        var customer = await factory.CreateClientForAsync(DemoUsers.Customer);
        var created = await customer.CreateTicketAsync();
        var me = await (await customer.GetAsync("/api/v1/auth/me")).ReadAsync<SupportAI.Contracts.Auth.UserInfoDto>();

        var response = await agent.PutJsonAsync($"/api/v1/tickets/{created.Id}/assignment", new AssignTicketRequest(me.Id, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
