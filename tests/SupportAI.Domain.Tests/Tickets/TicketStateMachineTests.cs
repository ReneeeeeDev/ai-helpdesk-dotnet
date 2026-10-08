using SupportAI.Domain.Tickets;

namespace SupportAI.Domain.Tests.Tickets;

public class TicketStateMachineTests
{
    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Triaged)]
    [InlineData(TicketStatus.New, TicketStatus.Open)]
    [InlineData(TicketStatus.Triaged, TicketStatus.Pending)]
    [InlineData(TicketStatus.Open, TicketStatus.Resolved)]
    [InlineData(TicketStatus.Pending, TicketStatus.Open)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Open)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Closed)]
    public void CanTransition_AllowedPairs_ReturnsTrue(TicketStatus from, TicketStatus to) =>
        TicketStateMachine.CanTransition(from, to).ShouldBeTrue();

    [Theory]
    [InlineData(TicketStatus.Closed, TicketStatus.Open)]
    [InlineData(TicketStatus.New, TicketStatus.Resolved)]
    [InlineData(TicketStatus.Open, TicketStatus.Triaged)]
    [InlineData(TicketStatus.Pending, TicketStatus.New)]
    public void CanTransition_ForbiddenPairs_ReturnsFalse(TicketStatus from, TicketStatus to) =>
        TicketStateMachine.CanTransition(from, to).ShouldBeFalse();

    [Fact]
    public void Closed_IsTerminal_HasNoOutgoingTransitions()
    {
        TicketStateMachine.IsTerminal(TicketStatus.Closed).ShouldBeTrue();
        TicketStateMachine.AllowedFrom(TicketStatus.Closed).ShouldBeEmpty();
    }

    [Fact]
    public void EveryStatus_HasDefinedTransitions()
    {
        foreach (var status in Enum.GetValues<TicketStatus>())
        {
            Should.NotThrow(() => TicketStateMachine.AllowedFrom(status));
        }
    }
}
