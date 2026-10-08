using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace SupportAI.Web.Services;

/// <summary>
/// Estado de autenticación de la UI a partir de la sesión (los claims vienen del perfil que devuelve la API).
/// La autorización real la hace siempre la API; esto solo decide qué se muestra.
/// </summary>
public sealed class ApiAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));
    private readonly SessionStore _session;

    public ApiAuthenticationStateProvider(SessionStore session)
    {
        _session = session;
        _session.Changed += OnSessionChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        await _session.EnsureLoadedAsync();
        var user = _session.User;
        if (user is null)
        {
            return Anonymous;
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("tenant", user.TenantName),
        ], authenticationType: "SupportAI");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    private void OnSessionChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
