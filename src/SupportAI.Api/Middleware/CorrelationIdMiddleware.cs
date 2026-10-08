using System.Diagnostics;
using Serilog.Context;

namespace SupportAI.Api.Middleware;

/// <summary>
/// Propaga <c>X-Correlation-Id</c>: lo toma del cliente (si es válido) o usa el TraceId de la actividad actual,
/// lo devuelve en la respuesta y lo añade a todos los logs del request.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 64 || !correlationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

/// <summary>Cabeceras de seguridad básicas para una API JSON.</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
