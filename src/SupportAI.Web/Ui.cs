using System.Globalization;
using MudBlazor;
using SupportAI.Contracts.Common;

namespace SupportAI.Web;

/// <summary>Tema, etiquetas en español y formato de valores para la UI.</summary>
public static class Ui
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4F46E5",
            Secondary = "#0EA5E9",
            AppbarBackground = "#1E1B4B",
            Background = "#F8FAFC",
            DrawerBackground = "#FFFFFF",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#818CF8",
            Secondary = "#38BDF8",
            AppbarBackground = "#111827",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px" },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "system-ui", "sans-serif"] },
        },
    };

    public static string StatusLabel(TicketStatus status) => status switch
    {
        TicketStatus.New => "Nuevo",
        TicketStatus.Triaged => "Clasificado",
        TicketStatus.Open => "Abierto",
        TicketStatus.Pending => "Pendiente cliente",
        TicketStatus.Resolved => "Resuelto",
        TicketStatus.Closed => "Cerrado",
        _ => status.ToString(),
    };

    public static Color StatusColor(TicketStatus status) => status switch
    {
        TicketStatus.New => Color.Info,
        TicketStatus.Triaged => Color.Secondary,
        TicketStatus.Open => Color.Primary,
        TicketStatus.Pending => Color.Warning,
        TicketStatus.Resolved => Color.Success,
        _ => Color.Default,
    };

    public static string PriorityLabel(TicketPriority priority) => priority switch
    {
        TicketPriority.Low => "Baja",
        TicketPriority.Normal => "Normal",
        TicketPriority.High => "Alta",
        TicketPriority.Urgent => "Urgente",
        _ => priority.ToString(),
    };

    public static Color PriorityColor(TicketPriority priority) => priority switch
    {
        TicketPriority.Urgent => Color.Error,
        TicketPriority.High => Color.Warning,
        TicketPriority.Normal => Color.Default,
        _ => Color.Dark,
    };

    public static string SentimentLabel(Sentiment? sentiment) => sentiment switch
    {
        Sentiment.VeryNegative => "Muy negativo",
        Sentiment.Negative => "Negativo",
        Sentiment.Neutral => "Neutral",
        Sentiment.Positive => "Positivo",
        _ => "—",
    };

    public static string RoleLabel(UserRole role) => role switch
    {
        UserRole.Admin => "Administrador",
        UserRole.Agent => "Agente",
        _ => "Cliente",
    };

    public static string TriageStatusLabel(TriageResultStatus status) => status switch
    {
        TriageResultStatus.Suggested => "Sugerido (requiere revisión)",
        TriageResultStatus.AutoApplied => "Aplicado automáticamente",
        TriageResultStatus.Accepted => "Aceptado por un agente",
        TriageResultStatus.Overridden => "Corregido por un agente",
        TriageResultStatus.Invalid => "Salida inválida",
        _ => status.ToString(),
    };

    public static string Relative(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "—";
        }

        var delta = value.Value - DateTimeOffset.UtcNow;
        var abs = delta.Duration();
        var text = abs.TotalMinutes < 1 ? "menos de 1 min"
            : abs.TotalHours < 1 ? $"{(int)abs.TotalMinutes} min"
            : abs.TotalDays < 1 ? $"{(int)abs.TotalHours} h"
            : $"{(int)abs.TotalDays} d";
        return delta < TimeSpan.Zero ? $"hace {text}" : $"en {text}";
    }

    public static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd MMM yyyy, HH:mm", Es) ?? "—";

    public static string Number(long value) => value.ToString("N0", Es);

    public static string Money(decimal value) => value.ToString("$#,##0.00##", CultureInfo.InvariantCulture);

    public static string Percent(double value) => value.ToString("P0", Es);
}
