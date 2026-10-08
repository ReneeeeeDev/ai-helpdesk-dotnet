using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using SupportAI.Contracts.Auth;

namespace SupportAI.Web.Services;

/// <summary>
/// Tokens de la sesión del usuario (uno por circuito de Blazor Server). Se persisten cifrados en
/// <see cref="ProtectedSessionStorage"/> para sobrevivir a una recarga de la pestaña, nunca en localStorage.
/// </summary>
public sealed class SessionStore(ProtectedSessionStorage storage, ILogger<SessionStore> logger)
{
    private const string Key = "supportai.session";
    private bool _loaded;

    public TokenResponse? Session { get; private set; }

    public string? AccessToken => Session?.AccessToken;

    public UserInfoDto? User => Session?.User;

    public bool IsStaff => User?.Role is Contracts.Common.UserRole.Agent or Contracts.Common.UserRole.Admin;

    public bool IsAdmin => User?.Role is Contracts.Common.UserRole.Admin;

    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            var result = await storage.GetAsync<TokenResponse>(Key);
            Session = result.Success ? result.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Datos de otra clave de cifrado o JS aún no disponible: se trata como sesión vacía.
            logger.LogDebug(ex, "No se pudo leer la sesión almacenada");
            Session = null;
        }

        _loaded = true;
    }

    public async Task SetAsync(TokenResponse session)
    {
        Session = session;
        _loaded = true;
        await storage.SetAsync(Key, session);
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        Session = null;
        _loaded = true;
        await storage.DeleteAsync(Key);
        Changed?.Invoke();
    }
}
