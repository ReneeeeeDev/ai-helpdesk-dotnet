using System.Text.RegularExpressions;

namespace SupportAI.Application.Features.Ai.Prompting;

public sealed record RedactionResult(string Text, int Emails, int Phones, int Cards, int Ibans)
{
    public bool FoundAny => Emails + Phones + Cards + Ibans > 0;
}

/// <summary>Elimina datos personales antes de enviar texto a un proveedor externo de LLM.</summary>
public interface IPiiRedactor
{
    RedactionResult Redact(string text);
}

/// <summary>
/// Redacción determinista y local (sin llamadas externas): emails, teléfonos, tarjetas (validadas con Luhn
/// para no borrar números de pedido) e IBAN. Se aplica a todo contenido de cliente antes de construir el prompt.
/// </summary>
public sealed partial class PiiRedactor : IPiiRedactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // Secuencias de 13-19 dígitos con espacios o guiones opcionales (candidatas a tarjeta).
    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){3,7}(?:[ ]?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iban();

    // Teléfonos: prefijo internacional opcional y 9 a 12 dígitos agrupados.
    [GeneratedRegex(@"(?<!\w)(?:\+\d{1,3}[ \-.]?)?(?:\(?\d{2,4}\)?[ \-.]?){2,4}\d{2,4}(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    public RedactionResult Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactionResult(text, 0, 0, 0, 0);
        }

        int emails = 0, cards = 0, ibans = 0, phones = 0;

        var result = Email().Replace(text, _ =>
        {
            emails++;
            return "[EMAIL]";
        });

        result = Iban().Replace(result, _ =>
        {
            ibans++;
            return "[IBAN]";
        });

        result = CardCandidate().Replace(result, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                cards++;
                return "[TARJETA]";
            }

            return m.Value;
        });

        result = Phone().Replace(result, m =>
        {
            var digitCount = m.Value.Count(char.IsDigit);
            if (digitCount is < 9 or > 15)
            {
                return m.Value; // fechas, importes o números cortos no son teléfonos
            }

            phones++;
            return "[TELÉFONO]";
        });

        return new RedactionResult(result, emails, phones, cards, ibans);
    }

    internal static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
