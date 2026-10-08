namespace SupportAI.Domain.Common;

/// <summary>Entidad con identidad. Dos entidades son iguales si tienen el mismo tipo e Id.</summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("El identificador de la entidad no puede ser vacío.");
        }

        Id = id;
    }

    /// <summary>Constructor para EF Core.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected set; }

    public bool Equals(Entity? other) =>
        other is not null && other.GetType() == GetType() && other.Id == Id && Id != Guid.Empty;

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) => Equals(left, right);

    public static bool operator !=(Entity? left, Entity? right) => !Equals(left, right);

    /// <summary>Ids ordenables en el tiempo (UUIDv7), útiles para paginación e índices.</summary>
    protected static Guid NewId() => Guid.CreateVersion7();
}
