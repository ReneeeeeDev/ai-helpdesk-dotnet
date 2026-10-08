using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SupportAI.Domain.Users;

namespace SupportAI.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "supportai";

    [Required]
    public string Audience { get; set; } = "supportai-clients";

    /// <summary>Clave HMAC (≥ 32 caracteres). En producción se inyecta por variable de entorno o secret manager.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;

    public SymmetricSecurityKey CreateSecurityKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

/// <summary>Nombres de claims propios del token (sin mapeo a los URIs largos de WS-Federation).</summary>
public static class SupportAIClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Name = "name";
    public const string Role = "role";
    public const string TenantId = "tenant_id";
    public const string TeamId = "team_id";
}

public sealed record IssuedTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, string RefreshTokenHash, DateTimeOffset RefreshTokenExpiresAt);

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedTokens Issue(User user)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var accessExpires = now.AddMinutes(settings.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(SupportAIClaims.UserId, user.Id.ToString()),
            new(SupportAIClaims.Email, user.Email),
            new(SupportAIClaims.Name, user.DisplayName),
            new(SupportAIClaims.Role, user.Role.ToString()),
            new(SupportAIClaims.TenantId, user.TenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (user.TeamId is { } teamId)
        {
            claims.Add(new Claim(SupportAIClaims.TeamId, teamId.ToString()));
        }

        var accessToken = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            SigningCredentials = new SigningCredentials(settings.CreateSecurityKey(), SecurityAlgorithms.HmacSha256),
        });

        var refreshToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        return new IssuedTokens(accessToken, accessExpires, refreshToken, HashRefreshToken(refreshToken), now.AddDays(settings.RefreshTokenDays));
    }

    public static string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
