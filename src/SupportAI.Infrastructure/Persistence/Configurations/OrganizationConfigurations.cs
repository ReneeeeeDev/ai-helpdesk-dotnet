using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportAI.Domain.Sla;
using SupportAI.Domain.Teams;
using SupportAI.Domain.Tenants;
using SupportAI.Domain.Users;

namespace SupportAI.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.ConfigureAggregate();
        builder.Property(t => t.Name).HasMaxLength(120).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(60).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
        builder.HasStringList(nameof(Tenant.Categories), "_categories");

        builder.OwnsOne(t => t.AiSettings, ai =>
        {
            ai.Property(s => s.AutoTriageEnabled).HasColumnName("Ai_AutoTriageEnabled");
            ai.Property(s => s.AutoApplyThreshold).HasColumnName("Ai_AutoApplyThreshold").HasPrecision(4, 3);
            ai.Property(s => s.MonthlyTokenBudget).HasColumnName("Ai_MonthlyTokenBudget");
        });
        builder.Navigation(t => t.AiSettings).IsRequired();
    }
}

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("Teams");
        builder.ConfigureAggregate();
        builder.Property(t => t.Name).HasMaxLength(80).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.HasStringList(nameof(Team.Categories), "_categories");
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.ConfigureAggregate();
        builder.Ignore(u => u.IsStaff);
        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
        builder.HasIndex(u => new { u.TenantId, u.Role });
    }
}

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("SlaPolicies");
        builder.ConfigureAggregate();
        builder.Property(p => p.Name).HasMaxLength(80).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.IsDefault });

        // Objetivos por prioridad como documento JSON: siempre se leen y escriben juntos.
        builder.OwnsMany(p => p.Targets, targets => targets.ToJson("Targets"));
        builder.Navigation(p => p.Targets).HasField("_targets").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
