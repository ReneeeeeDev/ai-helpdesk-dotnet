using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SupportAI.Api.Extensions;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Features.Auth;
using SupportAI.Contracts.Auth;

namespace SupportAI.Api.Controllers;

/// <summary>Autenticación con JWT de corta duración y refresh tokens rotativos.</summary>
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(ISender sender) : ApiControllerBase
{
    /// <summary>Inicia sesión. Tras 5 intentos fallidos la cuenta se bloquea 15 minutos.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new LoginCommand(request.Email, request.Password, ClientIp), ct));

    /// <summary>Obtiene un nuevo par de tokens. El refresh token usado queda invalidado.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshTokenRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new RefreshTokenCommand(request.RefreshToken, ClientIp), ct));

    /// <summary>Registro de un cliente final en una organización existente.</summary>
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TokenResponse>> Register(RegisterCustomerRequest request, CancellationToken ct)
    {
        var tokens = await sender.Send(new RegisterCustomerCommand(request.TenantSlug, request.Email, request.DisplayName, request.Password, ClientIp), ct);
        return Created("/api/v1/auth/me", tokens);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(request.RefreshToken), ct);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<UserInfoDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserInfoDto>> Me(CancellationToken ct) => Ok(await sender.Send(new GetMeQuery(), ct));
}
