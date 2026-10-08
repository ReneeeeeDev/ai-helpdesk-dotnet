using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Auth;
using SupportAI.Infrastructure.Persistence.Idempotency;

namespace SupportAI.Api.Idempotency;

/// <summary>
/// Habilita la cabecera <c>Idempotency-Key</c> en un endpoint POST:
/// - misma clave + mismo cuerpo → se devuelve la respuesta original (cabecera <c>Idempotent-Replayed: true</c>);
/// - misma clave + otro cuerpo → 422; misma clave aún en ejecución → 409.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) => ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
}

internal sealed class IdempotencyFilter(IdempotencyStore store, ICurrentUser user, IOptions<JsonOptions> jsonOptions) : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(key) || user.UserId is not { } userId)
        {
            await next();
            return;
        }

        if (key.Length > 100)
        {
            context.Result = Problem(StatusCodes.Status400BadRequest, "idempotency_key_invalid", "La Idempotency-Key no puede superar 100 caracteres.");
            return;
        }

        var serializerOptions = jsonOptions.Value.JsonSerializerOptions;
        var arguments = context.ActionArguments
            .Where(a => a.Value is not CancellationToken)
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .ToDictionary(a => a.Key, a => a.Value);
        var requestHash = Hash($"{http.Request.Method} {http.Request.Path} {JsonSerializer.Serialize(arguments, serializerOptions)}");
        var ct = http.RequestAborted;

        var (outcome, record) = await store.TryReserveAsync(userId, key, requestHash, ct);
        switch (outcome)
        {
            case ReservationOutcome.InProgress:
                context.Result = Problem(StatusCodes.Status409Conflict, "idempotency_in_progress", "Ya hay una solicitud en curso con esta Idempotency-Key.");
                return;

            case ReservationOutcome.Completed when record!.RequestHash != requestHash:
                context.Result = Problem(StatusCodes.Status422UnprocessableEntity, "idempotency_key_reused", "La Idempotency-Key ya se usó con un cuerpo distinto.");
                return;

            case ReservationOutcome.Completed:
                http.Response.Headers["Idempotent-Replayed"] = "true";
                if (record.Location is not null)
                {
                    http.Response.Headers.Location = record.Location;
                }

                context.Result = new ContentResult { StatusCode = record.StatusCode, Content = record.ResponseBody, ContentType = record.ContentType };
                return;
        }

        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            await store.ReleaseAsync(userId, key, CancellationToken.None);
            return;
        }

        if (executed.Result is ObjectResult { StatusCode: >= 200 and < 300 } result)
        {
            var body = JsonSerializer.Serialize(result.Value, serializerOptions);
            var location = (result as CreatedResult)?.Location;
            await store.CompleteAsync(userId, key, result.StatusCode ?? 200, body, "application/json; charset=utf-8", location, CancellationToken.None);
        }
        else
        {
            await store.ReleaseAsync(userId, key, CancellationToken.None);
        }
    }

    private static ObjectResult Problem(int status, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = "Idempotencia", Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
