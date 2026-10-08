using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Domain.Knowledge;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Teams;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;
using SupportAI.Infrastructure.Ai;
using SupportAI.Infrastructure.Identity;

namespace SupportAI.Infrastructure.Persistence.Seed;

/// <summary>
/// Aplica migraciones y carga datos de demostración (idempotente: no hace nada si ya existe el tenant demo).
/// </summary>
public sealed partial class DatabaseInitializer(
    SupportAIDbContext db,
    UserManager<AppUser> userManager,
    ITenantScope tenantScope,
    PromptCatalogSynchronizer promptSynchronizer,
    TimeProvider timeProvider,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseInitializer> logger)
{
    public const string DemoTenantSlug = "acme";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = options.Value;

        if (settings.ApplyMigrationsOnStartup)
        {
            LogApplyingMigrations(logger);
            await db.Database.MigrateAsync(ct);
        }

        // Los prompts versionados del repositorio se publican en la BD en cada arranque (idempotente).
        await promptSynchronizer.SyncAsync(ct);

        if (settings.SeedDemoData && !await db.Tenants.AnyAsync(t => t.Slug == DemoTenantSlug, ct))
        {
            await SeedDemoAsync(settings.DemoPassword, ct);
        }
    }

    private async Task SeedDemoAsync(string password, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        // --- Tenant principal ---
        var acme = Tenant.Create("Acme Cloud", DemoTenantSlug,
            ["acceso", "facturacion", "rendimiento", "integraciones", "cuenta", "bug", "consulta-general"]);
        var globex = Tenant.Create("Globex", "globex", ["soporte", "ventas"]);
        db.Tenants.AddRange(acme, globex);
        tenantScope.Use(acme.Id);

        var support = Team.Create(acme.Id, "Soporte Técnico", "Incidencias técnicas, acceso y rendimiento.", ["acceso", "rendimiento", "bug", "integraciones"]);
        var billing = Team.Create(acme.Id, "Facturación", "Cobros, reembolsos y datos de la cuenta.", ["facturacion", "cuenta"]);
        var general = Team.Create(acme.Id, "Atención General", "Consultas generales y primer contacto.", ["consulta-general"]);
        db.Teams.AddRange(support, billing, general);

        var sla = SlaPolicy.CreateStandard(acme.Id);
        db.SlaPolicies.AddRange(sla, SlaPolicy.CreateStandard(globex.Id));
        await db.SaveChangesAsync(ct);

        // --- Usuarios (perfil de dominio + credenciales de Identity con el mismo Id) ---
        var admin = await CreateUserAsync(acme.Id, "admin@acme.test", "Ana Administradora", UserRole.Admin, null, password, ct);
        var agent = await CreateUserAsync(acme.Id, "agente@acme.test", "Diego Soporte", UserRole.Agent, support.Id, password, ct);
        await CreateUserAsync(acme.Id, "facturacion@acme.test", "Lucía Facturación", UserRole.Agent, billing.Id, password, ct);
        var carlos = await CreateUserAsync(acme.Id, "cliente@acme.test", "Carlos Cliente", UserRole.Customer, null, password, ct);
        var maria = await CreateUserAsync(acme.Id, "cliente2@acme.test", "María López", UserRole.Customer, null, password, ct);
        await CreateUserAsync(globex.Id, "admin@globex.test", "Admin Globex", UserRole.Admin, null, password, ct);
        await CreateUserAsync(globex.Id, "cliente@globex.test", "Cliente Globex", UserRole.Customer, null, password, ct);

        // --- Base de conocimiento ---
        db.KnowledgeArticles.AddRange(KnowledgeBaseSeed.Articles(acme.Id, admin.Id));

        // --- Tickets de ejemplo (sus eventos TicketCreated alimentarán el triage vía outbox) ---
        var t1 = Ticket.Create(acme.Id, carlos.Id, "No puedo iniciar sesión después de activar 2FA",
            "Ayer activé la verificación en dos pasos y ahora el código de la app nunca es válido. Ya probé reinstalar la app. " +
            "Es urgente porque no puedo emitir facturas. Mi correo es carlos.cliente@example.com y mi teléfono +34 612 345 678.",
            TicketChannel.Web, sla, now.AddHours(-3));

        var t2 = Ticket.Create(acme.Id, carlos.Id, "Me cobraron dos veces la suscripción de octubre",
            "En el extracto de mi tarjeta 4111 1111 1111 1111 aparecen dos cargos de 49,90 € con fecha 1 de octubre. Necesito el reembolso de uno de ellos.",
            TicketChannel.Email, sla, now.AddHours(-26));

        var t3 = Ticket.Create(acme.Id, maria.Id, "El dashboard tarda más de 30 segundos en cargar",
            "Desde la actualización del lunes el panel principal tarda muchísimo, sobre todo el widget de métricas mensuales. Uso Chrome 130.",
            TicketChannel.Web, sla, now.AddDays(-2));
        t3.AddMessage(agent.Id, MessageAuthorType.Agent, "Hola María, ¿podrías indicarnos cuántos proyectos tienes y si ocurre también en modo incógnito?", false, now.AddDays(-2).AddHours(1));
        t3.ChangeStatus(TicketStatus.Pending, agent.Id, now.AddDays(-2).AddHours(1));

        var t4 = Ticket.Create(acme.Id, maria.Id, "Payments webhook returns 401 since yesterday",
            "Our payments webhook endpoint started receiving 401 responses from your API when we validate the signature. We rotated our secret last week. Is there any known issue?",
            TicketChannel.Api, sla, now.AddMinutes(-50));

        var t5 = Ticket.Create(acme.Id, carlos.Id, "¿Cómo exporto mis facturas en CSV?",
            "Necesito descargar todas las facturas del año para mi contador. ¿Se puede exportar en CSV o Excel?",
            TicketChannel.Chat, sla, now.AddMinutes(-15));

        db.Tickets.AddRange(t1, t2, t3, t4, t5);
        await db.SaveChangesAsync(ct);

        LogSeeded(logger, acme.Slug);
    }

    private async Task<User> CreateUserAsync(Guid tenantId, string email, string displayName, UserRole role, Guid? teamId, string password, CancellationToken ct)
    {
        var profile = User.Create(tenantId, email, displayName, role, teamId);
        db.Members.Add(profile);
        await db.SaveChangesAsync(ct);

        var credentials = new AppUser
        {
            Id = profile.Id,
            TenantId = tenantId,
            UserName = profile.Email,
            Email = profile.Email,
            EmailConfirmed = true,
        };
        var result = await userManager.CreateAsync(credentials, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"No se pudo crear el usuario demo {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }

        return profile;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Aplicando migraciones pendientes de la base de datos")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Datos de demostración creados para el tenant {TenantSlug}")]
    private static partial void LogSeeded(ILogger logger, string tenantSlug);
}
