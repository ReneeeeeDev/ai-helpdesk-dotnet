using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Infrastructure.Identity;

namespace SupportAI.Api.Middleware;

/// <summary>Fija el tenant del request a partir del claim <c>tenant_id</c> del JWT (después de UseAuthentication).</summary>
internal sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ITenantScope tenantScope)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(context.User.FindFirst(SupportAIClaims.TenantId)?.Value, out var tenantId))
        {
            tenantScope.Use(tenantId);
        }

        return next(context);
    }
}
