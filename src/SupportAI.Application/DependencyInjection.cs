using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Events;
using SupportAI.Application.Abstractions.Messaging;
using SupportAI.Application.Behaviors;
using SupportAI.Application.Features.Ai.Copilot;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Application.Features.Ai.Prompting;
using SupportAI.Application.Features.Ai.Summary;
using SupportAI.Application.Features.Ai.Triage;
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
        // Mensajes de validación en español sin depender de la cultura del servidor (CI corre en inglés).
        ValidatorOptions.Global.LanguageManager.Culture = new System.Globalization.CultureInfo("es");

        services.AddScoped<IKnowledgeSearch, KnowledgeSearch>();

        // IA
        services.AddSingleton<IPiiRedactor, PiiRedactor>();
        services.AddScoped<IPromptRegistry, PromptRegistry>();
        services.AddScoped<ILlmCallRecorder, LlmCallRecorder>();
        services.AddScoped<TokenBudgetGuard>();
        services.AddScoped<StructuredCompletionService>();
        services.AddScoped<TicketTriageService>();
        services.AddScoped<TicketSummaryService>();
        services.AddScoped<CopilotService>();

        return services;
    }

    /// <summary>Registra handlers de requests (CQRS) y de eventos de dominio (outbox) por escaneo del ensamblado.</summary>
    private static void RegisterHandlers(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        Type[] handlerInterfaces = [typeof(IRequestHandler<,>), typeof(IDomainEventHandler<>)];
        var registrations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && handlerInterfaces.Contains(i.GetGenericTypeDefinition()))
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in registrations)
        {
            services.AddScoped(service, implementation);
        }
    }
}
