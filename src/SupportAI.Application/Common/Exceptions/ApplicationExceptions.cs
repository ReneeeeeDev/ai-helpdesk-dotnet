namespace SupportAI.Application.Common.Exceptions;

/// <summary>El recurso no existe o no es visible para el usuario (→ 404).</summary>
public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' no encontrado.")
{
    public string Resource { get; } = resource;
}

/// <summary>El usuario está autenticado pero no autorizado para este recurso (→ 403).</summary>
public sealed class ForbiddenException(string message = "No tienes permiso para realizar esta acción.")
    : Exception(message);

/// <summary>Conflicto con el estado actual del recurso (→ 409).</summary>
public sealed class ConflictException(string message, string code = "conflict")
    : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Uno o más errores de validación de entrada (→ 400 ValidationProblemDetails).</summary>
public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("La solicitud tiene errores de validación.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Se agotó el presupuesto de IA del tenant o hay saturación del proveedor (→ 429).</summary>
public sealed class QuotaExceededException(string message, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>El proveedor de IA no está disponible temporalmente (→ 503).</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
