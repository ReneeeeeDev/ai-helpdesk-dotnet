var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "SupportAI Web");
await app.RunAsync();
