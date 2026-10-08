using SupportAI.Domain.Common;
using SupportAI.Domain.Tickets;

namespace SupportAI.Domain.Sla;

/// <summary>Objetivo de SLA para una prioridad (value object).</summary>
public sealed record SlaTarget
{
    public SlaTarget(TicketPriority priority, TimeSpan firstResponse, TimeSpan resolution)
    {
        Priority = priority;
        FirstResponse = Guard.Positive(firstResponse, nameof(firstResponse));
        Resolution = Guard.Positive(resolution, nameof(resolution));
        if (resolution < firstResponse)
        {
            throw new DomainException("El tiempo de resolución no puede ser menor que el de primera respuesta.", "sla_invalid");
        }
    }

    public TicketPriority Priority { get; init; }

    public TimeSpan FirstResponse { get; init; }

    public TimeSpan Resolution { get; init; }
}

/// <summary>Política de SLA de un tenant: un objetivo por prioridad. Calendario 24/7.</summary>
public sealed class SlaPolicy : AggregateRoot, ITenantOwned, IAuditable
{
    private List<SlaTarget> _targets = [];

    private SlaPolicy()
    {
    }

    private SlaPolicy(Guid id, Guid tenantId, string name, IEnumerable<SlaTarget> targets, bool isDefault)
        : base(id)
    {
        TenantId = Guard.NotEmpty(tenantId, nameof(tenantId));
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name), 80);
        IsDefault = isDefault;
        SetTargets(targets);
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public IReadOnlyList<SlaTarget> Targets => _targets.AsReadOnly();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public static SlaPolicy Create(Guid tenantId, string name, IEnumerable<SlaTarget> targets, bool isDefault = true) =>
        new(NewId(), tenantId, name, targets, isDefault);

    /// <summary>Política estándar razonable para un tenant nuevo.</summary>
    public static SlaPolicy CreateStandard(Guid tenantId) => Create(tenantId, "Estándar", [
        new SlaTarget(TicketPriority.Urgent, TimeSpan.FromMinutes(30), TimeSpan.FromHours(4)),
        new SlaTarget(TicketPriority.High, TimeSpan.FromHours(2), TimeSpan.FromHours(12)),
        new SlaTarget(TicketPriority.Normal, TimeSpan.FromHours(8), TimeSpan.FromDays(2)),
        new SlaTarget(TicketPriority.Low, TimeSpan.FromHours(24), TimeSpan.FromDays(5)),
    ]);

    public void SetTargets(IEnumerable<SlaTarget> targets)
    {
        var list = targets.ToList();
        var missing = Enum.GetValues<TicketPriority>().Except(list.Select(t => t.Priority)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainException($"Faltan objetivos de SLA para: {string.Join(", ", missing)}.", "sla_incomplete");
        }

        if (list.GroupBy(t => t.Priority).Any(g => g.Count() > 1))
        {
            throw new DomainException("Hay prioridades duplicadas en la política de SLA.", "sla_duplicated");
        }

        _targets = list;
    }

    public SlaTarget TargetFor(TicketPriority priority) => _targets.Single(t => t.Priority == priority);

    public void MarkAsDefault() => IsDefault = true;

    public void UnmarkAsDefault() => IsDefault = false;
}
