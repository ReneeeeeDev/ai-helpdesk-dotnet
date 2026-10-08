using Microsoft.EntityFrameworkCore;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Knowledge;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Teams;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Tickets;
using SupportAI.Domain.Users;

namespace SupportAI.Application.Abstractions.Data;

/// <summary>
/// Unidad de trabajo + acceso a datos para los casos de uso. Implementada por SupportAIDbContext.
/// Los conjuntos multi-tenant ya vienen filtrados por el tenant actual (filtro global de EF Core).
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Team> Teams { get; }

    /// <summary>Perfiles de usuario del dominio (las credenciales están en Identity).</summary>
    DbSet<User> Members { get; }

    DbSet<SlaPolicy> SlaPolicies { get; }

    DbSet<Ticket> Tickets { get; }

    DbSet<AiTriageResult> TriageResults { get; }

    DbSet<AiSuggestedReply> SuggestedReplies { get; }

    DbSet<KnowledgeArticle> KnowledgeArticles { get; }

    DbSet<PromptTemplate> PromptTemplates { get; }

    DbSet<LlmCallLog> LlmCalls { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
