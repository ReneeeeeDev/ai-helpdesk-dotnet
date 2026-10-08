using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Behaviors;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Domain.Users;

namespace SupportAI.Application.Tests.Messaging;

public sealed record PingQuery(string Value) : IQuery<string>;

[AuthorizeRoles(UserRole.Admin)]
public sealed record AdminOnlyCommand : ICommand;

[AllowAnonymousRequest]
public sealed record AnonymousQuery : IQuery<int>;

public sealed class PingHandler : IQueryHandler<PingQuery, string>
{
    public Task<string> Handle(PingQuery request, CancellationToken cancellationToken) => Task.FromResult($"pong:{request.Value}");
}

public sealed class AdminOnlyHandler : ICommandHandler<AdminOnlyCommand>
{
    public Task<Unit> Handle(AdminOnlyCommand request, CancellationToken cancellationToken) => Task.FromResult(Unit.Value);
}

public sealed class AnonymousHandler : IQueryHandler<AnonymousQuery, int>
{
    public Task<int> Handle(AnonymousQuery request, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed class PingValidator : AbstractValidator<PingQuery>
{
    public PingValidator() => RuleFor(q => q.Value).NotEmpty().MaximumLength(5);
}

/// <summary>Registra el orden de ejecución para verificar el anidamiento del pipeline.</summary>
public sealed class RecordingBehavior<TRequest, TResponse>(List<string> log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("before");
        var result = await next();
        log.Add("after");
        return result;
    }
}

public class SenderPipelineTests
{
    private static ServiceProvider BuildProvider(ICurrentUser user, List<string>? log = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(user);
        services.AddSingleton(log ?? []);
        services.AddScoped<ISender, Sender>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped<IRequestHandler<PingQuery, string>, PingHandler>();
        services.AddScoped<IRequestHandler<AdminOnlyCommand, Unit>, AdminOnlyHandler>();
        services.AddScoped<IRequestHandler<AnonymousQuery, int>, AnonymousHandler>();
        services.AddScoped<IValidator<PingQuery>, PingValidator>();
        return services.BuildServiceProvider();
    }

    private static ICurrentUser User(UserRole? role)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(role is not null);
        user.Role.Returns(role);
        user.UserId.Returns(role is null ? null : Guid.CreateVersion7());
        return user;
    }

    [Fact]
    public async Task Send_ValidRequest_RunsBehaviorsAroundHandler()
    {
        var log = new List<string>();
        await using var sp = BuildProvider(User(UserRole.Agent), log);

        var result = await sp.GetRequiredService<ISender>().Send(new PingQuery("hi"));

        result.ShouldBe("pong:hi");
        log.ShouldBe(["before", "after"]);
    }

    [Fact]
    public async Task Send_InvalidRequest_ThrowsValidationExceptionWithPropertyErrors()
    {
        await using var sp = BuildProvider(User(UserRole.Agent));

        var ex = await Should.ThrowAsync<RequestValidationException>(() => sp.GetRequiredService<ISender>().Send(new PingQuery("too-long-value")));

        ex.Errors.ShouldContainKey("Value");
    }

    [Fact]
    public async Task Send_Unauthenticated_ThrowsUnauthorized()
    {
        await using var sp = BuildProvider(User(null));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => sp.GetRequiredService<ISender>().Send(new PingQuery("hi")));
    }

    [Fact]
    public async Task Send_AnonymousRequest_SkipsAuthentication()
    {
        await using var sp = BuildProvider(User(null));

        (await sp.GetRequiredService<ISender>().Send(new AnonymousQuery())).ShouldBe(42);
    }

    [Theory]
    [InlineData(UserRole.Customer)]
    [InlineData(UserRole.Agent)]
    public async Task Send_RoleNotAllowed_ThrowsForbidden(UserRole role)
    {
        await using var sp = BuildProvider(User(role));

        await Should.ThrowAsync<ForbiddenException>(() => sp.GetRequiredService<ISender>().Send(new AdminOnlyCommand()));
    }

    [Fact]
    public async Task Send_AllowedRole_ExecutesHandler()
    {
        await using var sp = BuildProvider(User(UserRole.Admin));

        (await sp.GetRequiredService<ISender>().Send(new AdminOnlyCommand())).ShouldBe(Unit.Value);
    }
}
