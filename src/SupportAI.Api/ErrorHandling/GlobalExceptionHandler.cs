using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Common.Exceptions;
using SupportAI.Domain.Common;

namespace SupportAI.Api.ErrorHandling;

/// <summary>
/// Traduce excepciones a ProblemDetails (RFC 9457) con un <c>code</c> estable para los clientes.
/// Los errores no controlados devuelven 500 sin detalles internos (salvo en Development).
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code, detail) = exception switch
        {
            RequestValidationException => (StatusCodes.Status400BadRequest, "Solicitud inválida", "validation_failed", exception.Message),
            FluentValidation.ValidationException => (StatusCodes.Status400BadRequest, "Solicitud inválida", "validation_failed", exception.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Solicitud mal formada", "bad_request", exception.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "No autenticado", "unauthorized", exception.Message),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Acceso denegado", "forbidden", exception.Message),
            NotFoundException => (StatusCodes.Status404NotFound, "No encontrado", "not_found", exception.Message),
            ConflictException c => (StatusCodes.Status409Conflict, "Conflicto", c.Code, exception.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflicto de concurrencia", "concurrency_conflict",
                "El recurso fue modificado por otra operación. Recarga y vuelve a intentarlo."),
            DomainException d => (StatusCodes.Status422UnprocessableEntity, "Regla de negocio incumplida", d.Code, exception.Message),
            QuotaExceededException => (StatusCodes.Status429TooManyRequests, "Cuota excedida", "quota_exceeded", exception.Message),
            AiUnavailableException => (StatusCodes.Status503ServiceUnavailable, "IA no disponible", "ai_unavailable", exception.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (499, "Solicitud cancelada", "client_closed_request", "El cliente cerró la conexión."),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "internal_error",
                environment.IsDevelopment() ? exception.ToString() : "Ocurrió un error inesperado. Usa el traceId para reportarlo."),
        };

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        if (exception is QuotaExceededException { RetryAfter: { } retryAfter })
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        ProblemDetails problem = exception switch
        {
            RequestValidationException v => new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value)),
            FluentValidation.ValidationException fv => new ValidationProblemDetails(fv.Errors
                .GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),
            _ => new ProblemDetails(),
        };
        problem.Status = status;
        problem.Title = title;
        problem.Detail = detail;
        problem.Type = $"https://httpstatuses.io/{status}";
        problem.Extensions["code"] = code;

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Excepción no controlada en {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
