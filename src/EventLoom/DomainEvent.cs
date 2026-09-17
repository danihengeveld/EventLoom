namespace EventLoom;

public interface IDomainEvent;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    private int version = 1;

    public string Name { get; } = ValidateName(name);

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
