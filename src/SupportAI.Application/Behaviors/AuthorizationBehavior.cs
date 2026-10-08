using System.Reflection;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Common.Exceptions;

namespace SupportAI.Application.Behaviors;

/// <summary>
/// Exige un usuario autenticado salvo <see cref="AllowAnonymousRequestAttribute"/> y, si el request declara
/// <see cref="AuthorizeRolesAttribute"/>, que tenga uno de esos roles.
/// </summary>
internal sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly bool AllowAnonymous = typeof(TRequest).GetCustomAttribute<AllowAnonymousRequestAttribute>() is not null;
    private static readonly AuthorizeRolesAttribute? RolesAttribute = typeof(TRequest).GetCustomAttribute<AuthorizeRolesAttribute>();

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (AllowAnonymous)
        {
            return next();
        }

        if (!currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Se requiere autenticación.");
        }

        if (RolesAttribute is not null && (currentUser.Role is not { } role || !RolesAttribute.Roles.Contains(role)))
        {
            throw new ForbiddenException();
        }

        return next();
    }
}
