using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Infrastructure.Identity;
using SupportAI.Infrastructure.Persistence;
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

        return services;
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
