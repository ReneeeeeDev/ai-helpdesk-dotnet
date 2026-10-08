using SupportAI.Domain.Users;

namespace SupportAI.Application.Abstractions.Auth;

/// <summary>Usuario autenticado de la operación en curso (a partir de los claims del JWT).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    UserRole? Role { get; }

    Guid RequiredUserId => UserId ?? throw new InvalidOperationException("La operación requiere un usuario autenticado.");

    bool IsStaff => Role is UserRole.Agent or UserRole.Admin;

    bool IsAdmin => Role is UserRole.Admin;
}
