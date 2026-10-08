using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SupportAI.Infrastructure.Persistence.Idempotency;

public enum ReservationOutcome
{
    /// <summary>Clave nueva: el request debe ejecutarse.</summary>
    Reserved,

    /// <summary>Ya existe una respuesta completa para esta clave.</summary>
    Completed,

    /// <summary>Otro request con la misma clave está en curso.</summary>
    InProgress,
}

/// <summary>
/// Persistencia de claves de idempotencia. La reserva (INSERT con PK) evita que dos reintentos simultáneos
/// ejecuten el comando dos veces: solo uno gana la carrera.
/// </summary>
public sealed class IdempotencyStore(SupportAIDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<(ReservationOutcome Outcome, IdempotencyRecord? Record)> TryReserveAsync(Guid userId, string key, string requestHash, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            UserId = userId,
            Key = key,
            RequestHash = requestHash,
            StatusCode = 0,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return (ReservationOutcome.Reserved, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            db.ChangeTracker.Clear();
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstAsync(r => r.UserId == userId && r.Key == key, ct);
            return (existing.StatusCode == 0 ? ReservationOutcome.InProgress : ReservationOutcome.Completed, existing);
        }
    }

    public Task CompleteAsync(Guid userId, string key, int statusCode, string? body, string? contentType, string? location, CancellationToken ct) =>
        db.IdempotencyRecords.Where(r => r.UserId == userId && r.Key == key).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.StatusCode, statusCode)
            .SetProperty(r => r.ResponseBody, body)
            .SetProperty(r => r.ContentType, contentType)
            .SetProperty(r => r.Location, location), ct);

    /// <summary>Libera la reserva si el request falló, para que el cliente pueda reintentar con la misma clave.</summary>
    public Task ReleaseAsync(Guid userId, string key, CancellationToken ct) =>
        db.IdempotencyRecords.Where(r => r.UserId == userId && r.Key == key && r.StatusCode == 0).ExecuteDeleteAsync(ct);
}
