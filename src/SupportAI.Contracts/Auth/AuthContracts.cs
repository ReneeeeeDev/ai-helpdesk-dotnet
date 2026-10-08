using SupportAI.Contracts.Common;

namespace SupportAI.Contracts.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshTokenRequest(string RefreshToken);

public sealed record RegisterCustomerRequest(string TenantSlug, string Email, string DisplayName, string Password);

public sealed record UserInfoDto(Guid Id, string Email, string DisplayName, UserRole Role, Guid TenantId, string TenantName, Guid? TeamId);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserInfoDto User);
