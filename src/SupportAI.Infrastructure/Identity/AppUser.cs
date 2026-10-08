using Microsoft.AspNetCore.Identity;

namespace SupportAI.Infrastructure.Identity;

/// <summary>
/// Credenciales (ASP.NET Core Identity). Comparte Id con <see cref="Domain.Users.User"/>, que guarda el perfil y el rol.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }
}
