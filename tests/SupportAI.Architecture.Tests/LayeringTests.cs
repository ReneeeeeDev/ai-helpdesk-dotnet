using System.Reflection;
using NetArchTest.Rules;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Domain.Common;

namespace SupportAI.Architecture.Tests;

/// <summary>Reglas de Clean Architecture verificadas en cada build (ver AGENTS.md).</summary>
public class LayeringTests
{
    private const string Domain = "SupportAI.Domain";
    private const string Application = "SupportAI.Application";
    private const string Infrastructure = "SupportAI.Infrastructure";
    private const string Api = "SupportAI.Api";
    private const string Contracts = "SupportAI.Contracts";

    private static readonly Assembly DomainAssembly = typeof(Entity).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ISender).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(Contracts.Common.ApiProblem).Assembly;

    [Fact]
    public void Domain_DependsOnNothing() => AssertNoDependencies(DomainAssembly,
        Application, Infrastructure, Api, Contracts, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Anthropic", "FluentValidation");

    [Fact]
    public void Contracts_DependsOnNothing() => AssertNoDependencies(ContractsAssembly, Domain, Application, Infrastructure, Api);

    [Fact]
    public void Application_DoesNotDependOnInfrastructureWebOrLlmSdk() => AssertNoDependencies(ApplicationAssembly,
        Infrastructure, Api, "Microsoft.AspNetCore", "Anthropic", "Microsoft.EntityFrameworkCore.SqlServer");

    [Fact]
    public void Infrastructure_DoesNotDependOnApi() => AssertNoDependencies(InfrastructureAssembly, Api);

    [Fact]
    public void Controllers_DoNotTouchPersistenceDirectly()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That().ResideInNamespace("SupportAI.Api.Controllers")
            .ShouldNot().HaveDependencyOnAny("SupportAI.Infrastructure.Persistence", "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Handlers_AreSealedAndNotPublic()
    {
        var handlers = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .ToList();

        handlers.ShouldNotBeEmpty();
        handlers.Where(t => !t.IsSealed || t.IsPublic).Select(t => t.Name).ShouldBeEmpty();
    }

    [Fact]
    public void DomainEntities_HaveNoPublicSetters()
    {
        var offenders = DomainAssembly.GetTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Requests_AreImmutableRecords()
    {
        var mutable = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .SelectMany(t => t.GetProperties().Where(p => p.SetMethod is { IsPublic: true } setter &&
                !setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        mutable.ShouldBeEmpty();
    }

    private static void AssertNoDependencies(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    private static string Failing(TestResult result) =>
        "Tipos que violan la regla: " + string.Join(", ", result.FailingTypeNames ?? []);
}
