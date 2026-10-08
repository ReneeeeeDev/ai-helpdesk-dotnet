namespace SupportAI.Contracts.Common;

/// <summary>Página con paginación keyset: <see cref="NextCursor"/> es opaco y se envía tal cual para la siguiente página.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, int TotalCount);

/// <summary>Detalle de error estándar (RFC 9457) tal como lo devuelve la API.</summary>
public sealed record ApiProblem(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Code,
    string? TraceId,
    IReadOnlyDictionary<string, string[]>? Errors);
