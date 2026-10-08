using SupportAI.Contracts.Auth;

namespace SupportAI.Application.Abstractions.Auth;

/// <summary>Puerto de autenticación (implementado con ASP.NET Identity + JWT en Infrastructure).</summary>
public interface IAuthService
{
    /// <exception cref="UnauthorizedAccessException">Credenciales inválidas o cuenta bloqueada.</exception>
    Task<TokenResponse> LoginAsync(string email, string password, string? ipAddress, CancellationToken ct);

    /// <summary>Rota el refresh token. Reutilizar uno ya rotado revoca toda la sesión (detección de robo).</summary>
    Task<TokenResponse> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct);

    Task<TokenResponse> RegisterCustomerAsync(string tenantSlug, string email, string displayName, string password, string? ipAddress, CancellationToken ct);

    Task RevokeAsync(string refreshToken, CancellationToken ct);

    Task<UserInfoDto> GetUserInfoAsync(Guid userId, CancellationToken ct);
}
