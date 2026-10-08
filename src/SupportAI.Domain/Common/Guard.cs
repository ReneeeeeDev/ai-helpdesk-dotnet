namespace SupportAI.Domain.Common;

/// <summary>Validaciones de invariantes que lanzan <see cref="DomainException"/>.</summary>
public static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, string field, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"El campo '{field}' es obligatorio.", "field_required");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"El campo '{field}' supera el máximo de {maxLength} caracteres.", "field_too_long");
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException($"El campo '{field}' es obligatorio.", "field_required");
        }

        return value;
    }

    public static decimal InRange(decimal value, decimal min, decimal max, string field)
    {
        if (value < min || value > max)
        {
            throw new DomainException($"El campo '{field}' debe estar entre {min} y {max}.", "field_out_of_range");
        }

        return value;
    }

    public static TimeSpan Positive(TimeSpan value, string field)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new DomainException($"El campo '{field}' debe ser positivo.", "field_out_of_range");
        }

        return value;
    }

    /// <summary>Normaliza una lista de tags: minúsculas, sin vacíos, sin duplicados, máximo N.</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags, int max = 10, int maxLength = 40)
    {
        if (tags is null)
        {
            return [];
        }

        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Select(t => t.Length > maxLength ? t[..maxLength] : t)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }
}
