var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
await app.RunAsync();

/// <summary>Punto de entrada expuesto para WebApplicationFactory en los tests de integración.</summary>
public partial class Program;
