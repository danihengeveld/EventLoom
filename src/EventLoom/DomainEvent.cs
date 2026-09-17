namespace EventLoom;

/// <summary>Marks an immutable event raised by a domain aggregate.</summary>
public interface IDomainEvent;

/// <summary>Associates a stable persisted name and schema version with a domain event.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    private int version = 1;

    /// <summary>Gets the stable persisted event name.</summary>
    public string Name { get; } = ValidateName(name);

    /// <summary>Gets or sets the positive schema version.</summary>
    public int Version
    {
        get => version;
        init => version = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Event schema version must be positive.");
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
