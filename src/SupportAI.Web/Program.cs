using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using SupportAI.Web.Components;
using SupportAI.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    o.SnackbarConfiguration.VisibleStateDuration = 4000;
});

// Cliente HTTP hacia la API (servidor a servidor: Blazor Server corre en el backend).
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));
builder.Services.AddHttpClient(SupportAIApiClient.HttpClientName, (sp, client) =>
{
    client.BaseAddress = sp.GetRequiredService<IOptions<ApiOptions>>().Value.BaseUrl;
    client.Timeout = TimeSpan.FromMinutes(5); // streams de IA largos
});

// Servicios por circuito (un usuario = un circuito).
builder.Services.AddScoped<SessionStore>();
builder.Services.AddScoped<SupportAIApiClient>();
builder.Services.AddScoped<TicketHubClient>();
builder.Services.AddScoped<ApiAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ApiAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
