using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace SupportAI.Api.Streaming;

/// <summary>
/// Escribe un IAsyncEnumerable como Server-Sent Events (text/event-stream).
/// El primer elemento se obtiene ANTES de enviar cabeceras: así los errores tempranos (403, 404, 429 de presupuesto)
/// siguen saliendo como ProblemDetails normales. Un error a mitad del stream se envía como evento "error".
/// </summary>
public sealed partial class ServerSentEvents(IOptions<JsonOptions> jsonOptions, ILogger<ServerSentEvents> logger)
{
    public async Task WriteAsync<T>(HttpContext context, IAsyncEnumerable<T> source, Func<T, string> eventName, CancellationToken ct)
    {
        var json = jsonOptions.Value.JsonSerializerOptions;
        await using var enumerator = source.GetAsyncEnumerator(ct);

        var hasFirst = await enumerator.MoveNextAsync(); // las excepciones aquí → GlobalExceptionHandler

        var response = context.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no"; // evita el buffering de proxies como nginx
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(ct);

        try
        {
            var more = hasFirst;
            while (more)
            {
                var item = enumerator.Current;
                await WriteEventAsync(response, eventName(item), JsonSerializer.Serialize(item, json), ct);
                more = await enumerator.MoveNextAsync();
            }

            await WriteEventAsync(response, "end", "{}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El cliente cerró la conexión: nada que hacer.
        }
        catch (Exception ex)
        {
            LogStreamError(logger, ex);
            var payload = JsonSerializer.Serialize(new { message = "Se produjo un error generando la respuesta.", code = ex.GetType().Name }, json);
            await WriteEventAsync(response, "error", payload, CancellationToken.None);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error durante un stream SSE")]
    private static partial void LogStreamError(ILogger logger, Exception ex);
}
