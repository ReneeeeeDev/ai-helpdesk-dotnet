using System.Collections.Frozen;
using System.Text.Json;
using SupportAI.Domain.Common;

namespace SupportAI.Infrastructure.Persistence.Outbox;

/// <summary>
/// Mapea nombres de eventos ↔ tipos CLR. Usar el nombre corto (no el assembly-qualified) permite mover
/// o renombrar namespaces sin romper mensajes ya encolados.
/// </summary>
public static class DomainEventTypeRegistry
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly FrozenDictionary<string, Type> TypesByName = typeof(IDomainEvent).Assembly
        .GetTypes()
        .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IDomainEvent).IsAssignableFrom(t))
        .ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

    public static string NameOf(IDomainEvent domainEvent) => domainEvent.GetType().Name;

    public static string Serialize(IDomainEvent domainEvent) =>
        JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions);

    public static IDomainEvent Deserialize(string typeName, string payload)
    {
        if (!TypesByName.TryGetValue(typeName, out var type))
        {
            throw new InvalidOperationException($"Tipo de evento desconocido: {typeName}");
        }

        return (IDomainEvent)(JsonSerializer.Deserialize(payload, type, SerializerOptions)
            ?? throw new InvalidOperationException($"Payload vacío para el evento {typeName}"));
    }
}
