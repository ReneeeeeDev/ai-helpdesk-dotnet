using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupportAI.Domain.Common;

namespace SupportAI.Infrastructure.Persistence.Configurations;

internal static class ConfigurationExtensions
{
    /// <summary>Configuración común de raíces de agregado: clave, Id generado en el dominio, eventos no persistidos.</summary>
    public static void ConfigureAggregate<T>(this EntityTypeBuilder<T> builder)
        where T : AggregateRoot
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Ignore(e => e.DomainEvents);
    }

    /// <summary>Colección de strings (tags, categorías) almacenada como JSON en una columna nvarchar(max).</summary>
    public static void HasStringList<T>(this EntityTypeBuilder<T> builder, string propertyName, string fieldName)
        where T : class
    {
        builder.PrimitiveCollection<IReadOnlyList<string>>(propertyName)
            .HasField(fieldName)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .IsRequired();
    }
}
