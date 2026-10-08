using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SupportAI.Infrastructure.Tenancy;

namespace SupportAI.Infrastructure.Persistence;

/// <summary>
/// Usado solo por <c>dotnet ef</c> (migraciones). Toma la cadena de conexión de la variable de entorno
/// <c>ConnectionStrings__SupportAI</c> o usa LocalDB por defecto.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SupportAIDbContext>
{
    public SupportAIDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SupportAI")
            ?? DependencyInjection.DefaultLocalDbConnectionString;

        var options = new DbContextOptionsBuilder<SupportAIDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(DependencyInjection.SqlCompatibilityLevel))
            .Options;

        return new SupportAIDbContext(options, new TenantContext());
    }
}
