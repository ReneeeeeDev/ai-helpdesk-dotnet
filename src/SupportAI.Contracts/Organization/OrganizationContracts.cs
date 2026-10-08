using SupportAI.Contracts.Common;

namespace SupportAI.Contracts.Organization;

public sealed record TeamDto(Guid Id, string Name, string? Description, IReadOnlyList<string> Categories, int MemberCount);

public sealed record UpsertTeamRequest(string Name, string? Description, IReadOnlyList<string>? Categories);

public sealed record MemberDto(Guid Id, string Email, string DisplayName, UserRole Role, Guid? TeamId, bool IsActive);

public sealed record SlaTargetDto(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes);

public sealed record SlaPolicyDto(Guid Id, string Name, IReadOnlyList<SlaTargetDto> Targets);

public sealed record TenantSettingsDto(
    Guid Id,
    string Name,
    string Slug,
    IReadOnlyList<string> Categories,
    bool AutoTriageEnabled,
    decimal AutoApplyThreshold,
    long MonthlyTokenBudget);

public sealed record UpdateTenantSettingsRequest(
    IReadOnlyList<string> Categories,
    bool AutoTriageEnabled,
    decimal AutoApplyThreshold,
    long MonthlyTokenBudget);
