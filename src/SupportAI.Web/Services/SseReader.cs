using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SupportAI.Web.Services;

/// <summary>Evento Server-Sent Events: nombre + datos JSON.</summary>
public sealed record SseEvent(string Event, JsonElement Data)
{
    public string? GetString(string property) =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Parser mínimo de text/event-stream (líneas "event:" y "data:", eventos separados por línea en blanco).</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventName = "message";
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var doc = JsonDocument.Parse(data.ToString());
                    yield return new SseEvent(eventName, doc.RootElement.Clone());
                }

                eventName = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }
    }
}
