using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace SupportAI.Api.Controllers;

/// <summary>Base de los controllers v1: rutas versionadas, respuestas JSON y ProblemDetails para errores.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Extrae la versión del encabezado If-Match (ETag) para concurrencia optimista.</summary>
    protected string? IfMatchVersion =>
        Request.Headers.IfMatch.FirstOrDefault()?.Trim().TrimStart('W', '/').Trim('"') is { Length: > 0 } v ? v : null;

    protected string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
