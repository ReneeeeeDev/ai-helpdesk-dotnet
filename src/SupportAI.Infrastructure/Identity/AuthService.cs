using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Application.Common.Mapping;
using SupportAI.Contracts.Auth;
using SupportAI.Domain.Users;
using SupportAI.Infrastructure.Persistence;

namespace SupportAI.Infrastructure.Identity;

/// <summary>Login con bloqueo por intentos, emisión de JWT y refresh tokens rotativos con detección de reutilización.</summary>
internal sealed partial class AuthService(
    SupportAIDbContext db,
    UserManager<AppUser> userManager,
    JwtTokenService tokens,
    TimeProvider clock,
    ILogger<AuthService> logger) : IAuthService
{
    private const string InvalidCredentials = "Email o contraseña incorrectos.";

    public async Task<TokenResponse> LoginAsync(string email, string password, string? ipAddress, CancellationToken ct)
    {
        var credentials = await userManager.FindByEmailAsync(email);
        if (credentials is null)
        {
            throw new UnauthorizedAccessException(InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(credentials))
        {
            LogLockedOut(logger, credentials.Id);
            throw new UnauthorizedAccessException("Cuenta bloqueada temporalmente por intentos fallidos. Inténtalo más tarde.");
        }

        if (!await userManager.CheckPasswordAsync(credentials, password))
        {
            await userManager.AccessFailedAsync(credentials);
            throw new UnauthorizedAccessException(InvalidCredentials);
        }

        await userManager.ResetAccessFailedCountAsync(credentials);

        var profile = await LoadActiveProfileAsync(credentials.Id, ct);
        return await IssueAsync(profile, ipAddress, ct);
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = JwtTokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedAccessException("Refresh token inválido.");

        if (stored.RevokedAt is not null)
        {
            if (stored.ReplacedByTokenHash is not null)
            {
                // Token ya rotado reutilizado: alguien más lo tiene. Se revoca toda la sesión del usuario.
                LogRefreshReuse(logger, stored.UserId);
                await db.RefreshTokens
                    .Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            }

            throw new UnauthorizedAccessException("Refresh token revocado.");
        }

        if (stored.ExpiresAt <= now)
        {
            throw new UnauthorizedAccessException("Refresh token expirado.");
        }

        var profile = await LoadActiveProfileAsync(stored.UserId, ct);
        var issued = tokens.Issue(profile);

        stored.RevokedAt = now;
        stored.ReplacedByTokenHash = issued.RefreshTokenHash;
        db.RefreshTokens.Add(NewRefreshToken(profile.Id, issued, ipAddress, now));
        await db.SaveChangesAsync(ct);

        return await BuildResponseAsync(profile, issued, ct);
    }

    public async Task<TokenResponse> RegisterCustomerAsync(string tenantSlug, string email, string displayName, string password, string? ipAddress, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == tenantSlug.Trim().ToLowerInvariant(), ct)
            ?? throw new RequestValidationException(new Dictionary<string, string[]> { ["tenantSlug"] = ["La organización no existe."] });

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            throw new ConflictException("Ya existe una cuenta con ese email.", "email_taken");
        }

        var profile = User.Create(tenant.Id, email, displayName, UserRole.Customer);

        // Perfil de dominio + credenciales en una única transacción (con la estrategia de reintentos de SQL Server).
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Members.Add(profile);
            await db.SaveChangesAsync(ct);

            var result = await userManager.CreateAsync(
                new AppUser { Id = profile.Id, TenantId = tenant.Id, UserName = profile.Email, Email = profile.Email, EmailConfirmed = true },
                password);
            if (!result.Succeeded)
            {
                throw new RequestValidationException(new Dictionary<string, string[]>
                {
                    ["password"] = result.Errors.Select(e => e.Description).ToArray(),
                });
            }

            await transaction.CommitAsync(ct);
        });

        return await IssueAsync(profile, ipAddress, ct);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = JwtTokenService.HashRefreshToken(refreshToken);
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<UserInfoDto> GetUserInfoAsync(Guid userId, CancellationToken ct)
    {
        var profile = await LoadActiveProfileAsync(userId, ct);
        var tenantName = await db.Tenants.Where(t => t.Id == profile.TenantId).Select(t => t.Name).FirstAsync(ct);
        return ToUserInfo(profile, tenantName);
    }

    private async Task<User> LoadActiveProfileAsync(Guid userId, CancellationToken ct)
    {
        // Login/refresh ocurren antes de conocer el tenant: se ignora el filtro global de forma explícita.
        var profile = await db.Members.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == userId, ct);
        if (profile is null || !profile.IsActive)
        {
            throw new UnauthorizedAccessException("La cuenta no existe o está desactivada.");
        }

        return profile;
    }

    private async Task<TokenResponse> IssueAsync(User profile, string? ipAddress, CancellationToken ct)
    {
        var issued = tokens.Issue(profile);
        db.RefreshTokens.Add(NewRefreshToken(profile.Id, issued, ipAddress, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return await BuildResponseAsync(profile, issued, ct);
    }

    private async Task<TokenResponse> BuildResponseAsync(User profile, IssuedTokens issued, CancellationToken ct)
    {
        var tenantName = await db.Tenants.Where(t => t.Id == profile.TenantId).Select(t => t.Name).FirstAsync(ct);
        return new TokenResponse(issued.AccessToken, issued.AccessTokenExpiresAt, issued.RefreshToken, issued.RefreshTokenExpiresAt, ToUserInfo(profile, tenantName));
    }

    private static RefreshToken NewRefreshToken(Guid userId, IssuedTokens issued, string? ip, DateTimeOffset now) => new()
    {
        UserId = userId,
        TokenHash = issued.RefreshTokenHash,
        CreatedAt = now,
        ExpiresAt = issued.RefreshTokenExpiresAt,
        CreatedByIp = ip,
    };

    private static UserInfoDto ToUserInfo(User u, string tenantName) =>
        new(u.Id, u.Email, u.DisplayName, u.Role.ToContract(), u.TenantId, tenantName, u.TeamId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intento de login sobre cuenta bloqueada {UserId}")]
    private static partial void LogLockedOut(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reutilización de refresh token detectada para {UserId}: se revocan todas sus sesiones")]
    private static partial void LogRefreshReuse(ILogger logger, Guid userId);
}
