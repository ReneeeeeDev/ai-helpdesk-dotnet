using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportAI.Application.Abstractions.Messaging;

namespace SupportAI.Application.Behaviors;

/// <summary>Mide y registra cada request. Avisa de los lentos (más de 500 ms).</summary>
internal sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var start = Stopwatch.GetTimestamp();
        try
        {
            var response = await next();
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed > SlowThreshold)
            {
                ApplicationLogs.SlowRequest(logger, name, elapsed.TotalMilliseconds);
            }
            else
            {
                ApplicationLogs.RequestHandled(logger, name, elapsed.TotalMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            ApplicationLogs.RequestFailed(logger, name, ex.GetType().Name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            throw;
        }
    }
}

internal static partial class ApplicationLogs
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "{RequestName} completado en {ElapsedMs:0.0} ms")]
    public static partial void RequestHandled(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RequestName} lento: {ElapsedMs:0.0} ms")]
    public static partial void SlowRequest(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RequestName} falló con {ExceptionType} tras {ElapsedMs:0.0} ms")]
    public static partial void RequestFailed(ILogger logger, string requestName, string exceptionType, double elapsedMs);
}
