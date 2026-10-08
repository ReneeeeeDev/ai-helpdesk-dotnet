using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SupportAI.Api.Auth;
using SupportAI.Api.ErrorHandling;
using SupportAI.Api.OpenApi;
using SupportAI.Api.Realtime;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Application.Abstractions.Notifications;
using SupportAI.Infrastructure.Identity;
using SupportAI.Infrastructure.Persistence;

namespace SupportAI.Api.Extensions;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Ai = "ai";
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int RequestsPerUserPer10Seconds { get; set; } = 50;

    public int AuthRequestsPerIpPerMinute { get; set; } = 10;

    public int AiRequestsPerTenantPerMinute { get; set; } = 30;
}

internal static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<ITicketNotifier, SignalRTicketNotifier>();

        services.AddRouting(o => o.LowercaseUrls = true);
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Instance = $"{ctx.HttpContext.Request.Method} {ctx.HttpContext.Request.Path}";
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        AddJwtAuthentication(services);
        AddVersioningAndOpenApi(services);
        AddRateLimiting(services, configuration);

        services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddHealthChecks().AddDbContextCheck<SupportAIDbContext>("database", tags: ["ready"]);

        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
                .WithExposedHeaders("ETag", "Location", "X-Correlation-Id", "Idempotent-Replayed", "Retry-After");
        }));

        return services;
    }

    private static void AddJwtAuthentication(IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // La configuración del bearer depende de JwtOptions (validadas al arrancar).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwt, clock) =>
            {
                var settings = jwt.Value;
                var clockSkew = TimeSpan.FromSeconds(30);
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = settings.CreateSecurityKey(),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = clockSkew,
                    NameClaimType = SupportAIClaims.Name,
                    RoleClaimType = SupportAIClaims.Role,
                    // Emisión y validación usan el mismo reloj (TimeProvider): coherente y testeable con FakeTimeProvider.
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = clock.GetUtcNow().UtcDateTime;
                        return (notBefore is null || notBefore.Value <= now + clockSkew)
                            && (expires is null || expires.Value > now - clockSkew);
                    },
                };
                bearer.Events = new JwtBearerEvents
                {
                    // SignalR (WebSockets) no admite cabeceras: el token viaja en la query string solo para el hub.
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(TicketsHub.Path))
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Staff, p => p.RequireAuthenticatedUser().RequireRole("Agent", "Admin"))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole("Admin"));
    }

    private static void AddVersioningAndOpenApi(IServiceCollection services)
    {
        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.ReportApiVersions = true;
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });

        services.AddOpenApi("v1", o => o.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
    }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Global: token bucket por usuario (o IP si es anónimo).
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetTokenBucketLimiter(
                    ctx.User.FindFirst(SupportAIClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.RequestsPerUserPer10Seconds * 2,
                        TokensPerPeriod = limits.RequestsPerUserPer10Seconds,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            // Login/registro: ventana fija por IP contra fuerza bruta.
            o.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AuthRequestsPerIpPerMinute, Window = TimeSpan.FromMinutes(1) }));

            // IA: por tenant, para proteger el presupuesto y el rate limit del proveedor.
            o.AddPolicy(RateLimitPolicies.Ai, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirst(SupportAIClaims.TenantId)?.Value ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AiRequestsPerTenantPerMinute, Window = TimeSpan.FromMinutes(1) }));

            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Demasiadas solicitudes",
                    Detail = "Superaste el límite de solicitudes. Espera unos segundos y vuelve a intentarlo.",
                };
                problem.Extensions["code"] = "rate_limited";
                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext { HttpContext = context.HttpContext, ProblemDetails = problem });
            };
        });
    }
}
