namespace SupportAI.Infrastructure.Identity;

/// <summary>
/// Refresh token con rotación. Solo se guarda el hash SHA-256; el valor en claro lo tiene únicamente el cliente.
/// Si se reutiliza un token ya rotado (posible robo), se revoca toda la familia del usuario.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? ReplacedByTokenHash { get; set; }

    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
