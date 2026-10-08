using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportAI.Domain.Ai;
using SupportAI.Domain.Knowledge;

namespace SupportAI.Infrastructure.Persistence.Configurations;

internal sealed class AiTriageResultConfiguration : IEntityTypeConfiguration<AiTriageResult>
{
    public void Configure(EntityTypeBuilder<AiTriageResult> builder)
    {
        builder.ToTable("AiTriageResults");
        builder.ConfigureAggregate();
        builder.Property(r => r.PromptKey).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Model).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Priority).HasConversion<int>();
        builder.Property(r => r.Category).HasMaxLength(60);
        builder.Property(r => r.Language).HasMaxLength(10);
        builder.Property(r => r.Summary).HasMaxLength(1000);
        builder.Property(r => r.SuggestedTeam).HasMaxLength(80);
        builder.Property(r => r.Reasoning).HasMaxLength(2000);
        builder.Property(r => r.ReviewNotes).HasMaxLength(1000);
        builder.Property(r => r.Confidence).HasPrecision(4, 3);
        builder.HasStringList(nameof(AiTriageResult.Tags), "_tags");

        builder.HasIndex(r => new { r.TenantId, r.TicketId, r.CreatedAt });
        builder.HasIndex(r => new { r.PromptKey, r.PromptVersion, r.Status });
    }
}

internal sealed class AiSuggestedReplyConfiguration : IEntityTypeConfiguration<AiSuggestedReply>
{
    public void Configure(EntityTypeBuilder<AiSuggestedReply> builder)
    {
        builder.ToTable("AiSuggestedReplies");
        builder.ConfigureAggregate();
        builder.Property(r => r.Body).IsRequired();
        builder.Property(r => r.Confidence).HasPrecision(4, 3);
        builder.PrimitiveCollection<IReadOnlyList<Guid>>(nameof(AiSuggestedReply.CitedArticleIds))
            .HasField("_citedArticleIds").UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();
        builder.HasStringList(nameof(AiSuggestedReply.FollowUpQuestions), "_followUpQuestions");
        builder.HasIndex(r => new { r.TenantId, r.TicketId, r.CreatedAt });
    }
}

internal sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("PromptTemplates");
        builder.ConfigureAggregate();
        builder.Property(p => p.Key).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.SystemPrompt).IsRequired();
        builder.Property(p => p.UserTemplate).IsRequired();
        builder.Property(p => p.Model).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Effort).HasMaxLength(10).IsRequired();
        builder.Property(p => p.ContentHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(p => new { p.Key, p.Version }).IsUnique();
        // Garantía a nivel de BD: como máximo una versión activa por prompt.
        builder.HasIndex(p => p.Key).IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_PromptTemplates_ActiveKey");
    }
}

internal sealed class LlmCallLogConfiguration : IEntityTypeConfiguration<LlmCallLog>
{
    public void Configure(EntityTypeBuilder<LlmCallLog> builder)
    {
        builder.ToTable("LlmCallLogs");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Ignore(l => l.TotalTokens);
        builder.Property(l => l.Operation).HasMaxLength(40).IsRequired();
        builder.Property(l => l.PromptKey).HasMaxLength(100);
        builder.Property(l => l.Model).HasMaxLength(100).IsRequired();
        builder.Property(l => l.StopReason).HasMaxLength(40);
        builder.Property(l => l.ErrorType).HasMaxLength(100);
        builder.Property(l => l.ErrorMessage).HasMaxLength(1000);
        builder.Property(l => l.CorrelationId).HasMaxLength(64);
        builder.Property(l => l.EstimatedCostUsd).HasPrecision(18, 6);

        builder.HasIndex(l => new { l.TenantId, l.CreatedAt });
        builder.HasIndex(l => new { l.Operation, l.CreatedAt });
    }
}

internal sealed class KnowledgeArticleConfiguration : IEntityTypeConfiguration<KnowledgeArticle>
{
    public void Configure(EntityTypeBuilder<KnowledgeArticle> builder)
    {
        builder.ToTable("KnowledgeArticles");
        builder.ConfigureAggregate();
        builder.Property(a => a.Title).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Body).IsRequired();
        builder.Property(a => a.Category).HasMaxLength(60);
        builder.HasStringList(nameof(KnowledgeArticle.Tags), "_tags");
        builder.HasIndex(a => new { a.TenantId, a.IsPublished, a.Category });
    }
}
