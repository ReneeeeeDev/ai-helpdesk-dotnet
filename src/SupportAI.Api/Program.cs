using Serilog;
using SupportAI.Api.Extensions;
using SupportAI.Application;
using SupportAI.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "SupportAI.Api"));

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddApiServices(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline();
await app.InitializeDatabaseAsync();
await app.RunAsync();

/// <summary>Punto de entrada expuesto para WebApplicationFactory en los tests de integración.</summary>
public partial class Program;
