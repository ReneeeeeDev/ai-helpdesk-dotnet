using SupportAI.Domain.Common;

namespace SupportAI.Domain.Users;

public enum UserRole
{
    Customer = 0,
    Agent = 1,
    Admin = 2,
}

/// <summary>
/// Perfil de una persona dentro de un tenant. Las credenciales viven aparte (ASP.NET Identity, mismo Id),
/// así el dominio no depende de Identity.
/// </summary>
public sealed class User : AggregateRoot, ITenantOwned, IAuditable
{
    private User()
    {
    }

    private User(Guid id, Guid tenantId, string email, string displayName, UserRole role, Guid? teamId)
        : base(id)
    {
        TenantId = Guard.NotEmpty(tenantId, nameof(tenantId));
        Email = NormalizeEmail(email);
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), 100);
        Role = role;
        TeamId = role == UserRole.Customer ? null : teamId;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    public Guid? TeamId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsStaff => Role is UserRole.Agent or UserRole.Admin;

    public static User Create(Guid tenantId, string email, string displayName, UserRole role, Guid? teamId = null, Guid? id = null) =>
        new(id ?? NewId(), tenantId, email, displayName, role, teamId);

    public void AssignToTeam(Guid? teamId)
    {
        if (!IsStaff && teamId is not null)
        {
            throw new DomainException("Solo los agentes pueden pertenecer a un equipo.", "user_team_not_allowed");
        }

        TeamId = teamId;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string NormalizeEmail(string email)
    {
        var value = Guard.NotNullOrWhiteSpace(email, nameof(email), 256).ToLowerInvariant();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == value.Length - 1 || value.IndexOf('@', at + 1) >= 0)
        {
            throw new DomainException("El email no es válido.", "user_email_invalid");
        }

        return value;
    }
}
