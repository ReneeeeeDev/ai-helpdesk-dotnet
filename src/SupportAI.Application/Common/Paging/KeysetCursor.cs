using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace SupportAI.Application.Common.Paging;

/// <summary>
/// Cursor opaco para paginación keyset: (clave de orden opcional, número de ticket como desempate).
/// Se codifica en Base64Url para que el cliente no dependa de su formato.
/// </summary>
public sealed record KeysetCursor(long Number, long? Key)
{
    public string Encode()
    {
        var raw = Key is null
            ? Number.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Number}:{Key}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static KeysetCursor? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = raw.Split(':');
            var number = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long? key = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null;
            return new KeysetCursor(number, key);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
