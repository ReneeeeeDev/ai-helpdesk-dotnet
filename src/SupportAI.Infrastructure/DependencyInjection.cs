using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Tenancy;
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

        return services;
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
                sql.EnableRetryOnFailure(maxRetryCount: 3);
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
