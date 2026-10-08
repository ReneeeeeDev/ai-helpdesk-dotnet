using SupportAI.Domain.Common;

namespace SupportAI.Domain.Tenants;

/// <summary>
/// Configuración de IA por tenant (value object, se persiste como owned type).
/// </summary>
public sealed record TenantAiSettings
{
    public TenantAiSettings(bool autoTriageEnabled, decimal autoApplyThreshold, long monthlyTokenBudget)
    {
        AutoTriageEnabled = autoTriageEnabled;
        AutoApplyThreshold = Guard.InRange(autoApplyThreshold, 0m, 1m, nameof(autoApplyThreshold));
        if (monthlyTokenBudget < 0)
        {
            throw new DomainException("El presupuesto de tokens no puede ser negativo.", "field_out_of_range");
        }

        MonthlyTokenBudget = monthlyTokenBudget;
    }

    public static TenantAiSettings Default => new(autoTriageEnabled: true, autoApplyThreshold: 0.80m, monthlyTokenBudget: 5_000_000);

    /// <summary>Si es false, los tickets nuevos no se envían al LLM automáticamente.</summary>
    public bool AutoTriageEnabled { get; init; }

    /// <summary>Confianza mínima (0..1) para aplicar el triage sin revisión humana.</summary>
    public decimal AutoApplyThreshold { get; init; }

    /// <summary>Tokens (entrada + salida) permitidos por mes calendario. 0 = sin límite.</summary>
    public long MonthlyTokenBudget { get; init; }

    public bool ShouldAutoApply(decimal confidence, bool requiresHuman) =>
        !requiresHuman && confidence >= AutoApplyThreshold;
}
