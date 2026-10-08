using SupportAI.Domain.Common;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Tickets.Events;

namespace SupportAI.Domain.Tests.Tickets;

public class TicketTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly Guid AgentId = Guid.CreateVersion7();
    private static readonly SlaPolicy Policy = SlaPolicy.CreateStandard(TenantId);

    private static Ticket NewTicket(TicketPriority priority = TicketPriority.Normal) =>
        Ticket.Create(TenantId, CustomerId, "No puedo iniciar sesión", "Desde ayer me da error 500 al entrar.", TicketChannel.Web, Policy, T0, priority);

    [Fact]
    public void Create_ValidData_StartsNewWithSlaAndRaisesCreatedEvent()
    {
        var ticket = NewTicket();

        ticket.Status.ShouldBe(TicketStatus.New);
        ticket.FirstResponseDueAt.ShouldBe(T0.AddHours(8));
        ticket.ResolutionDueAt.ShouldBe(T0.AddDays(2));
        ticket.DomainEvents.OfType<TicketCreated>().ShouldHaveSingleItem().TicketId.ShouldBe(ticket.Id);
        ticket.Activities.ShouldHaveSingleItem().Type.ShouldBe(TicketActivityType.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptySubject_Throws(string subject) =>
        Should.Throw<DomainException>(() => Ticket.Create(TenantId, CustomerId, subject, "desc", TicketChannel.Web, Policy, T0))
            .Code.ShouldBe("field_required");

    [Fact]
    public void Create_WithoutPolicy_HasNoSlaDates()
    {
        var ticket = Ticket.Create(TenantId, CustomerId, "Asunto", "Descripción", TicketChannel.Email, null, T0);

        ticket.FirstResponseDueAt.ShouldBeNull();
        ticket.ResolutionDueAt.ShouldBeNull();
    }

    [Fact]
    public void ChangeStatus_InvalidTransition_ThrowsWithCode()
    {
        var ticket = NewTicket();

        var ex = Should.Throw<DomainException>(() => ticket.ChangeStatus(TicketStatus.Resolved, AgentId, T0));

        ex.Code.ShouldBe("ticket_invalid_transition");
    }

    [Fact]
    public void ChangeStatus_SameStatus_IsNoOp()
    {
        var ticket = NewTicket();
        ticket.ClearDomainEvents();

        ticket.ChangeStatus(TicketStatus.New, AgentId, T0);

        ticket.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void AddMessage_FirstPublicAgentReply_SetsFirstResponseAndOpens()
    {
        var ticket = NewTicket();

        ticket.AddMessage(AgentId, MessageAuthorType.Agent, "Hola, lo revisamos.", isInternal: false, T0.AddMinutes(30));

        ticket.FirstRespondedAt.ShouldBe(T0.AddMinutes(30));
        ticket.Status.ShouldBe(TicketStatus.Open);
        ticket.DomainEvents.OfType<TicketMessageAdded>().ShouldHaveSingleItem();
    }

    [Fact]
    public void AddMessage_InternalNote_DoesNotCountAsFirstResponse()
    {
        var ticket = NewTicket();

        ticket.AddMessage(AgentId, MessageAuthorType.Agent, "Nota: revisar logs", isInternal: true, T0.AddMinutes(5));

        ticket.FirstRespondedAt.ShouldBeNull();
        ticket.Status.ShouldBe(TicketStatus.New);
    }

    [Fact]
    public void AddMessage_CustomerInternalNote_Throws() =>
        Should.Throw<DomainException>(() => NewTicket().AddMessage(CustomerId, MessageAuthorType.Customer, "x", isInternal: true, T0))
            .Code.ShouldBe("ticket_internal_note_forbidden");

    [Fact]
    public void AddMessage_CustomerRepliesWhilePending_ReopensTicket()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(TicketStatus.Open, AgentId, T0);
        ticket.ChangeStatus(TicketStatus.Pending, AgentId, T0.AddHours(1));

        ticket.AddMessage(CustomerId, MessageAuthorType.Customer, "Ya probé eso y sigue fallando", isInternal: false, T0.AddHours(2));

        ticket.Status.ShouldBe(TicketStatus.Open);
    }

    [Fact]
    public void ClosedTicket_RejectsChanges()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(TicketStatus.Closed, AgentId, T0);

        Should.Throw<DomainException>(() => ticket.AddMessage(CustomerId, MessageAuthorType.Customer, "hola", false, T0)).Code.ShouldBe("ticket_closed");
        Should.Throw<DomainException>(() => ticket.Assign(AgentId, null, AgentId, T0)).Code.ShouldBe("ticket_closed");
        ticket.ClosedAt.ShouldBe(T0);
    }

    [Fact]
    public void Sla_PausesWhilePending_AndShiftsResolutionDueOnResume()
    {
        var ticket = NewTicket();
        var originalDue = ticket.ResolutionDueAt!.Value;
        ticket.ChangeStatus(TicketStatus.Open, AgentId, T0);

        ticket.ChangeStatus(TicketStatus.Pending, AgentId, T0.AddHours(1));
        ticket.SlaPausedAt.ShouldBe(T0.AddHours(1));

        ticket.ChangeStatus(TicketStatus.Open, AgentId, T0.AddHours(4));

        ticket.SlaPausedAt.ShouldBeNull();
        ticket.SlaPausedDuration.ShouldBe(TimeSpan.FromHours(3));
        ticket.ResolutionDueAt.ShouldBe(originalDue.AddHours(3));
    }

    [Fact]
    public void Resolve_ThenReopen_ClearsResolvedAt()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(TicketStatus.Open, AgentId, T0);
        ticket.ChangeStatus(TicketStatus.Resolved, AgentId, T0.AddHours(1));
        ticket.ResolvedAt.ShouldBe(T0.AddHours(1));

        ticket.ChangeStatus(TicketStatus.Open, CustomerId, T0.AddHours(2));

        ticket.ResolvedAt.ShouldBeNull();
    }

    [Fact]
    public void EvaluateSla_FirstResponseOverdue_MarksBreachOnlyOnce()
    {
        var ticket = NewTicket(TicketPriority.Urgent); // primera respuesta: 30 min
        ticket.ClearDomainEvents();

        ticket.EvaluateSla(T0.AddMinutes(31)).ShouldBeTrue();
        ticket.EvaluateSla(T0.AddMinutes(45)).ShouldBeFalse();

        ticket.FirstResponseBreached.ShouldBeTrue();
        ticket.DomainEvents.OfType<TicketSlaBreached>().ShouldHaveSingleItem().Kind.ShouldBe("first_response");
    }

    [Fact]
    public void EvaluateSla_WhilePaused_DoesNotBreachResolution()
    {
        var ticket = NewTicket(TicketPriority.Urgent); // resolución: 4 h
        ticket.AddMessage(AgentId, MessageAuthorType.Agent, "Respuesta", false, T0.AddMinutes(10));
        ticket.ChangeStatus(TicketStatus.Pending, AgentId, T0.AddMinutes(20));

        ticket.EvaluateSla(T0.AddHours(10)).ShouldBeFalse();

        ticket.ResolutionBreached.ShouldBeFalse();
    }

    [Fact]
    public void ApplyTriage_ChangesPriority_RecalculatesSlaAndMovesToTriaged()
    {
        var ticket = NewTicket();
        var teamId = Guid.CreateVersion7();
        var decision = new TriageDecision("Acceso", TicketPriority.Urgent, Sentiment.Negative, "ES", teamId, ["Login", "login", "error-500"], "No puede iniciar sesión.");

        ticket.ApplyTriage(decision, Policy, actorId: null, byAi: true, T0.AddMinutes(1));

        ticket.Status.ShouldBe(TicketStatus.Triaged);
        ticket.Category.ShouldBe("acceso");
        ticket.Language.ShouldBe("es");
        ticket.Priority.ShouldBe(TicketPriority.Urgent);
        ticket.TeamId.ShouldBe(teamId);
        ticket.Tags.ShouldBe(["login", "error-500"]);
        ticket.FirstResponseDueAt.ShouldBe(T0.AddMinutes(30));
        ticket.ResolutionDueAt.ShouldBe(T0.AddHours(4));
        ticket.DomainEvents.OfType<TicketTriaged>().ShouldHaveSingleItem().ByAi.ShouldBeTrue();
        ticket.DomainEvents.OfType<TicketPriorityChanged>().ShouldHaveSingleItem();
    }

    [Fact]
    public void ApplyTriage_ClearsHumanReviewFlag()
    {
        var ticket = NewTicket();
        ticket.FlagForHumanReview("baja confianza", T0);

        ticket.ApplyTriage(new TriageDecision("acceso", TicketPriority.Normal, null, null, null, [], null), Policy, AgentId, byAi: false, T0);

        ticket.NeedsHumanReview.ShouldBeFalse();
    }
}
