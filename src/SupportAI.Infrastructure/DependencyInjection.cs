using Anthropic;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Application.Features.Ai.Llm;
using SupportAI.Infrastructure.Ai;
using SupportAI.Infrastructure.Ai.Claude;
using SupportAI.Infrastructure.Ai.Fake;
using SupportAI.Infrastructure.BackgroundJobs;
using SupportAI.Infrastructure.Identity;
using SupportAI.Infrastructure.Persistence;
using SupportAI.Infrastructure.Persistence.Idempotency;
using SupportAI.Infrastructure.Persistence.Interceptors;
using SupportAI.Infrastructure.Persistence.Seed;
using SupportAI.Infrastructure.Tenancy;

namespace SupportAI.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "SupportAI";

    public const string DefaultLocalDbConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=SupportAI;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False";

    /// <summary>LocalDB 2019 = 150. EF Core usa este nivel para decidir qué funciones JSON puede generar.</summary>
    public const int SqlCompatibilityLevel = 150;

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        AddPersistence(services, configuration);
        AddIdentityCore(services);
        AddAuthentication(services, configuration);
        AddBackgroundJobs(services, configuration);
        AddAi(services, configuration);

        return services;
    }

    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName));

        // Precios por MTok (USD) para estimar costos en LlmCallLog; sobrescribibles por configuración.
        services.Configure<LlmPricingOptions>(o =>
        {
            o.Models["claude-opus-5-5"] = new ModelPricing { InputPerMTok = 4m, OutputPerMTok = 20m, CacheWritePerMTok = 5m, CacheReadPerMTok = 0.20m };
            o.Models["claude-sonnet-5-5"] = new ModelPricing { InputPerMTok = 2m, OutputPerMTok = 10m, CacheWritePerMTok = 2.5m, CacheReadPerMTok = 0.20m };
            o.Models["claude-haiku-4-5"] = new ModelPricing { InputPerMTok = 1m, OutputPerMTok = 5m, CacheWritePerMTok = 1.25m, CacheReadPerMTok = 0.10m };
        });
        services.Configure<LlmPricingOptions>(configuration.GetSection(LlmPricingOptions.SectionName));

        services.AddSingleton<IJsonSchemaValidator, JsonSchemaValidator>();
        services.AddScoped<PromptCatalogSynchronizer>();

        var provider = configuration.GetValue(LlmOptions.SectionName + ":" + nameof(LlmOptions.Provider), LlmProvider.Fake);
        if (provider == LlmProvider.Claude)
        {
            services.AddSingleton(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
                // Sin ApiKey explícita, el SDK resuelve ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN / perfil de `ant auth login`.
                return string.IsNullOrWhiteSpace(settings.ApiKey)
                    ? new AnthropicClient { MaxRetries = settings.MaxRetries, Timeout = settings.RequestTimeout }
                    : new AnthropicClient { ApiKey = settings.ApiKey, MaxRetries = settings.MaxRetries, Timeout = settings.RequestTimeout };
            });
            services.AddSingleton<ClaudeLlmClient>();
            services.AddSingleton<ILlmClient>(sp => ActivatorUtilities.CreateInstance<ResilientLlmClient>(sp, sp.GetRequiredService<ClaudeLlmClient>()));
        }
        else
        {
            services.AddSingleton<ILlmClient, FakeLlmClient>();
        }
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IdempotencyStore>();
    }

    private static void AddBackgroundJobs(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));

        // Singletons + hosted: los tests pueden resolverlos e invocar ProcessBatchAsync/CheckAsync de forma determinista.
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
        services.AddSingleton<SlaMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SlaMonitor>());
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) ?? DefaultLocalDbConnectionString;

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantScope>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<DomainEventsToOutboxInterceptor>();

        services.AddDbContext<SupportAIDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                // Azure SQL serverless puede tardar ~1 min en reanudarse tras una pausa automática.
                sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
            });
            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<DomainEventsToOutboxInterceptor>());
        });
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SupportAIDbContext>());

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddScoped<DatabaseInitializer>();
    }

    private static void AddIdentityCore(IServiceCollection services)
    {
        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<SupportAIDbContext>();
    }
}
