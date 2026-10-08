using SupportAI.Infrastructure;
using SupportAI.Infrastructure.Persistence.Seed;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
await app.RunAsync();

/// <summary>Punto de entrada expuesto para WebApplicationFactory en los tests de integración.</summary>
public partial class Program;
