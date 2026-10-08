namespace SupportAI.Infrastructure.Persistence.Seed;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Aplica migraciones pendientes al arrancar (recomendado solo en desarrollo; en producción se usa el script idempotente del pipeline).</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Crea un tenant demo con usuarios, equipos, KB y tickets de ejemplo si la BD está vacía.</summary>
    public bool SeedDemoData { get; set; }

    /// <summary>Contraseña de los usuarios demo (solo desarrollo).</summary>
    public string DemoPassword { get; set; } = "Passw0rd!";
}
