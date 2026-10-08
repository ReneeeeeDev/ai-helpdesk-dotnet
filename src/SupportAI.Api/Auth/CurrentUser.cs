using System.Security.Claims;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Domain.Users;
using SupportAI.Infrastructure.Identity;

namespace SupportAI.Api.Auth;

/// <summary>Usuario actual a partir de los claims del JWT. Fuera de un request HTTP (workers) es anónimo.</summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId is not null;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(SupportAIClaims.UserId), out var id) ? id : null;

    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(SupportAIClaims.Role), out var role) ? role : null;
}

public static class Policies
{
    public const string Staff = "staff";
    public const string Admin = "admin";
}
