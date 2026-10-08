using C = SupportAI.Contracts.Common;
using DAi = SupportAI.Domain.Ai;
using DT = SupportAI.Domain.Tickets;
using DU = SupportAI.Domain.Users;

namespace SupportAI.Application.Common.Mapping;

/// <summary>
/// Conversión entre enums de dominio y de contrato. Comparten nombres y valores
/// (lo verifica <c>EnumMappingTests</c>), así que la conversión es un cast numérico seguro.
/// </summary>
public static class EnumMapping
{
    public static C.TicketStatus ToContract(this DT.TicketStatus value) => (C.TicketStatus)(int)value;

    public static DT.TicketStatus ToDomain(this C.TicketStatus value) => (DT.TicketStatus)(int)value;

    public static C.TicketPriority ToContract(this DT.TicketPriority value) => (C.TicketPriority)(int)value;

    public static DT.TicketPriority ToDomain(this C.TicketPriority value) => (DT.TicketPriority)(int)value;

    public static C.TicketChannel ToContract(this DT.TicketChannel value) => (C.TicketChannel)(int)value;

    public static DT.TicketChannel ToDomain(this C.TicketChannel value) => (DT.TicketChannel)(int)value;

    public static C.Sentiment? ToContract(this DT.Sentiment? value) => value is null ? null : (C.Sentiment)(int)value.Value;

    public static C.MessageAuthorType ToContract(this DT.MessageAuthorType value) => (C.MessageAuthorType)(int)value;

    public static C.UserRole ToContract(this DU.UserRole value) => (C.UserRole)(int)value;

    public static C.TriageResultStatus ToContract(this DAi.TriageResultStatus value) => (C.TriageResultStatus)(int)value;
}
