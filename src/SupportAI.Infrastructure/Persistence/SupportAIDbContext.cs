using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Application.Abstractions.Tenancy;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Common;
using SupportAI.Domain.Knowledge;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Teams;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;
using SupportAI.Infrastructure.Identity;
using SupportAI.Infrastructure.Persistence.Idempotency;
using SupportAI.Infrastructure.Persistence.Outbox;

namespace SupportAI.Infrastructure.Persistence;

/// <summary>
/// DbContext único: dominio, Identity (solo usuarios, sin roles: el rol vive en el perfil de dominio),
/// outbox, idempotencia y refresh tokens. Así el registro de usuario y el outbox comparten la misma transacción.
/// </summary>
public sealed class SupportAIDbContext(DbContextOptions<SupportAIDbContext> options, ITenantContext tenantContext)
    : IdentityUserContext<AppUser, Guid>(options), IApplicationDbContext
{
    public const string TicketNumberSequence = "TicketNumbers";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<User> Members => Set<User>();

    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<AiTriageResult> TriageResults => Set<AiTriageResult>();

    public DbSet<AiSuggestedReply> SuggestedReplies => Set<AiSuggestedReply>();

    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();

    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();

    public DbSet<LlmCallLog> LlmCalls => Set<LlmCallLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>
    /// Tenant usado por los filtros globales. EF Core lo evalúa por instancia de contexto (se parametriza en el SQL).
    /// Guid.Empty cuando no hay tenant ⇒ las consultas filtradas no devuelven filas.
    /// </summary>
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasSequence<long>(TicketNumberSequence).StartsAt(1000).IncrementsBy(1);
        builder.ApplyConfigurationsFromAssembly(typeof(SupportAIDbContext).Assembly);

        ConfigureIdentityTables(builder);
        ApplyGlobalFilters(builder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums como texto: legibles en SQL y estables ante reordenamientos del enum.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
    }

    private static void ConfigureIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>(b =>
        {
            b.ToTable("IdentityUsers");
            b.HasIndex(u => u.TenantId);
        });
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("IdentityUserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("IdentityUserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("IdentityUserTokens");
    }

    /// <summary>
    /// Combina en un único filtro (EF Core 9 admite uno por entidad) el aislamiento por tenant y el borrado lógico.
    /// </summary>
    private void ApplyGlobalFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (entityType.IsOwned() || clrType.IsAbstract)
            {
                continue;
            }

            var isTenantOwned = typeof(ITenantOwned).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
            if (!isTenantOwned && !isSoftDeletable)
            {
                continue;
            }

            var parameter = Expression.Parameter(clrType, "e");
            Expression? body = null;

            if (isTenantOwned)
            {
                var tenantProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));
                var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
                body = Expression.Equal(tenantProperty, currentTenant);
            }

            if (isSoftDeletable)
            {
                var notDeleted = Expression.Not(Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));
                body = body is null ? notDeleted : Expression.AndAlso(body, notDeleted);
            }

            entityType.SetQueryFilter(Expression.Lambda(body!, parameter));
        }
    }
}
