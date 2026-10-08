using FluentValidation;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Contracts.Auth;

namespace SupportAI.Application.Features.Auth;

[AllowAnonymousRequest]
public sealed record LoginCommand(string Email, string Password, string? IpAddress) : ICommand<TokenResponse>;

[AllowAnonymousRequest]
public sealed record RefreshTokenCommand(string RefreshToken, string? IpAddress) : ICommand<TokenResponse>;

[AllowAnonymousRequest]
public sealed record RegisterCustomerCommand(string TenantSlug, string Email, string DisplayName, string Password, string? IpAddress) : ICommand<TokenResponse>;

[AllowAnonymousRequest]
public sealed record LogoutCommand(string RefreshToken) : ICommand;

public sealed record GetMeQuery : IQuery<UserInfoDto>;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(200);
    }
}

internal sealed class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator() => RuleFor(c => c.RefreshToken).NotEmpty().MaximumLength(200);
}

internal sealed class RegisterCustomerValidator : AbstractValidator<RegisterCustomerCommand>
{
    public RegisterCustomerValidator()
    {
        RuleFor(c => c.TenantSlug).NotEmpty().MaximumLength(60);
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Password).NotEmpty().MinimumLength(8).MaximumLength(200);
    }
}

internal sealed class LoginHandler(IAuthService auth) : ICommandHandler<LoginCommand, TokenResponse>
{
    public Task<TokenResponse> Handle(LoginCommand c, CancellationToken ct) => auth.LoginAsync(c.Email, c.Password, c.IpAddress, ct);
}

internal sealed class RefreshTokenHandler(IAuthService auth) : ICommandHandler<RefreshTokenCommand, TokenResponse>
{
    public Task<TokenResponse> Handle(RefreshTokenCommand c, CancellationToken ct) => auth.RefreshAsync(c.RefreshToken, c.IpAddress, ct);
}

internal sealed class RegisterCustomerHandler(IAuthService auth) : ICommandHandler<RegisterCustomerCommand, TokenResponse>
{
    public Task<TokenResponse> Handle(RegisterCustomerCommand c, CancellationToken ct) =>
        auth.RegisterCustomerAsync(c.TenantSlug, c.Email, c.DisplayName, c.Password, c.IpAddress, ct);
}

internal sealed class LogoutHandler(IAuthService auth) : ICommandHandler<LogoutCommand>
{
    public async Task<Unit> Handle(LogoutCommand c, CancellationToken ct)
    {
        await auth.RevokeAsync(c.RefreshToken, ct);
        return Unit.Value;
    }
}

internal sealed class GetMeHandler(IAuthService auth, ICurrentUser user) : IQueryHandler<GetMeQuery, UserInfoDto>
{
    public Task<UserInfoDto> Handle(GetMeQuery query, CancellationToken ct) => auth.GetUserInfoAsync(user.RequiredUserId, ct);
}
