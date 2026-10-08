using System.Globalization;
using System.Text;

namespace SupportAI.Application.Common.Text;

public static class TextNormalizer
{
    /// <summary>Minúsculas y sin tildes, para comparar "Facturación" con "facturacion".</summary>
    public static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
