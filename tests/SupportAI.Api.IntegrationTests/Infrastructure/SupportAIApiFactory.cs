using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Respawn;
using SupportAI.Contracts.Auth;
using SupportAI.Infrastructure.Persistence;
using SupportAI.Infrastructure.Persistence.Seed;
using SupportAI.Infrastructure.Tenancy;

namespace SupportAI.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Levanta la API completa contra un SQL Server real (LocalDB en local; el servicio mssql en GitLab CI vía
/// <c>SUPPORTAI_TEST_CONNECTION</c>). La BD se recrea al iniciar la colección y se aplican migraciones + seed demo.
/// Los workers están desactivados: los tests los invocan de forma determinista.
/// </summary>
public sealed class SupportAIApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string DemoPassword = "Passw0rd!";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("SUPPORTAI_TEST_CONNECTION")
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=SupportAI_IntegrationTests;Trusted_Connection=True;TrustServerCertificate=True";

    private Respawner? _respawner;

    /// <summary>Reloj controlable: los tests de SLA lo adelantan para provocar incumplimientos.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (y no ConfigureAppConfiguration) para que Program vea los valores al registrar servicios.
        builder.UseSetting("ConnectionStrings:SupportAI", ConnectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Database:SeedDemoData", "true");
        builder.UseSetting("Database:DemoPassword", DemoPassword);
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("RateLimiting:RequestsPerUserPer10Seconds", "10000");
        builder.UseSetting("RateLimiting:AuthRequestsPerIpPerMinute", "10000");
        builder.UseSetting("RateLimiting:AiRequestsPerTenantPerMinute", "10000");
        builder.UseSetting("Llm:Provider", "Fake");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public async Task InitializeAsync()
    {
        // BD limpia por ejecución: el arranque de la API aplica migraciones y carga el seed.
        var options = new DbContextOptionsBuilder<SupportAIDbContext>().UseSqlServer(ConnectionString).Options;
        await using (var db = new SupportAIDbContext(options, new TenantContext()))
        {
            await db.Database.EnsureDeletedAsync();
        }

        _ = Server; // fuerza el arranque del host

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <summary>Vacía todas las tablas y vuelve a cargar el seed demo (para tests que necesitan conteos exactos).</summary>
    public async Task ResetDatabaseAsync()
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await _respawner!.ResetAsync(connection);
        }

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }

    public async Task<HttpClient> CreateClientForAsync(string email)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DemoPassword), Json);
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<SupportAIApiFactory>
{
    public const string Name = "Api";
}

public static class DemoUsers
{
    public const string Admin = "admin@acme.test";
    public const string Agent = "agente@acme.test";
    public const string BillingAgent = "facturacion@acme.test";
    public const string Customer = "cliente@acme.test";
    public const string OtherCustomer = "cliente2@acme.test";
    public const string GlobexAdmin = "admin@globex.test";
}
