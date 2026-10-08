using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Behaviors;
using SupportAI.Application.Features.Knowledge;

namespace SupportAI.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<ISender, Sender>();

        // Orden del pipeline: Logging → Authorization → Validation → Handler.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        RegisterHandlers(services, assembly);
        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: true);

        services.AddScoped<IKnowledgeSearch, KnowledgeSearch>();

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        var handlerInterface = typeof(IRequestHandler<,>);
        var registrations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface)
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in registrations)
        {
            services.AddScoped(service, implementation);
        }
    }
}
