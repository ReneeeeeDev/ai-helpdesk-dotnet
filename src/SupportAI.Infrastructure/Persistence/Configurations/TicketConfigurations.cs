using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportAI.Domain.Tickets;

namespace SupportAI.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");
        builder.ConfigureAggregate();
        builder.Ignore(t => t.IsSlaBreached);

        builder.Property(t => t.Number)
            .HasDefaultValueSql($"NEXT VALUE FOR {SupportAIDbContext.TicketNumberSequence}")
            .ValueGeneratedOnAdd();

        builder.Property(t => t.Subject).HasMaxLength(Ticket.MaxSubjectLength).IsRequired();
        builder.Property(t => t.Description).IsRequired();
        // Prioridad como entero (no texto) para que ORDER BY respete la severidad.
        builder.Property(t => t.Priority).HasConversion<int>();
        builder.Property(t => t.Category).HasMaxLength(60);
        builder.Property(t => t.Language).HasMaxLength(10);
        builder.Property(t => t.AiSummary).HasMaxLength(1000);
        builder.HasStringList(nameof(Ticket.Tags), "_tags");

        // TimeSpan → bigint (ticks): la columna 'time' de SQL Server no admite más de 24 h.
        builder.Property(t => t.SlaPausedDuration).HasConversion<long>();

        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Messages).HasField("_messages").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(t => t.Activities).WithOne().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Activities).HasField("_activities").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status, t.Priority });
        builder.HasIndex(t => new { t.TenantId, t.AssigneeId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.RequesterId });
        // Paginación keyset del inbox: ORDER BY CreatedAt DESC, Id DESC.
        builder.HasIndex(t => new { t.TenantId, t.CreatedAt, t.Id }).IsDescending(false, true, true);
        // Monitor de SLA: solo tickets abiertos con vencimiento.
        builder.HasIndex(t => new { t.Status, t.ResolutionDueAt })
            .HasFilter("[IsDeleted] = 0 AND [Status] IN ('New','Triaged','Open','Pending')")
            .HasDatabaseName("IX_Tickets_SlaWatch");
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable("TicketMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Body).IsRequired();
        builder.HasIndex(m => new { m.TicketId, m.CreatedAt });
    }
}

internal sealed class TicketActivityConfiguration : IEntityTypeConfiguration<TicketActivity>
{
    public void Configure(EntityTypeBuilder<TicketActivity> builder)
    {
        builder.ToTable("TicketActivities");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Description).HasMaxLength(500).IsRequired();
        builder.HasIndex(a => new { a.TicketId, a.OccurredAt });
    }
}
