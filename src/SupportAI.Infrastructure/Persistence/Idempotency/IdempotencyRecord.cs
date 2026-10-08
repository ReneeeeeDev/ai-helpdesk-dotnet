namespace SupportAI.Infrastructure.Persistence.Idempotency;

/// <summary>
/// Respuesta guardada para una combinación (usuario, Idempotency-Key). Un reintento del cliente con la misma
/// clave y el mismo cuerpo recibe la misma respuesta sin volver a ejecutar el comando.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid UserId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 de método + ruta + cuerpo: detecta reutilizar la clave con otro payload (→ 422).</summary>
    public string RequestHash { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string? ResponseBody { get; set; }

    public string? ContentType { get; set; }

    public string? Location { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
