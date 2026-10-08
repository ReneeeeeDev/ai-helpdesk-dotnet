using SupportAI.Domain.Users;

namespace SupportAI.Application.Abstractions.Messaging;

/// <summary>
/// Restringe un request a ciertos roles. Se verifica en <c>AuthorizationBehavior</c>, además de las políticas HTTP,
/// para que el caso de uso esté protegido aunque se invoque desde otro punto de entrada.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuthorizeRolesAttribute(params UserRole[] roles) : Attribute
{
    public IReadOnlyList<UserRole> Roles { get; } = roles;
}

/// <summary>El request no requiere usuario autenticado (login, registro, refresh).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AllowAnonymousRequestAttribute : Attribute;
