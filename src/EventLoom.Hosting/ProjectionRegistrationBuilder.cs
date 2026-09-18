using EventLoom.EntityFrameworkCore;

namespace EventLoom.Hosting;

/// <summary>
/// Registers the handlers for one durable projection identity.
/// </summary>
public sealed class ProjectionRegistrationBuilder
{
    private readonly EventLoomBuilder eventLoom;
    private readonly ProjectionKey key;
    private int registrationCount;

    internal ProjectionRegistrationBuilder(EventLoomBuilder eventLoom, ProjectionKey key)
    {
        this.eventLoom = eventLoom ?? throw new ArgumentNullException(nameof(eventLoom));
        this.key = key ?? throw new ArgumentNullException(nameof(key));
    }

    /// <summary>
    /// Registers a typed asynchronous, at-least-once projection handler.
    /// </summary>
    /// <typeparam name="TProjection">The projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <returns>This projection registration builder.</returns>
    public ProjectionRegistrationBuilder Asynchronous<TProjection, TEvent>()
        where TProjection : class, IProjectionHandler<TEvent>
    {
        eventLoom.AddProjection<TProjection, TEvent>(key.Name, key.Version);
        registrationCount++;
        return this;
    }

    /// <summary>
    /// Registers a typed projection handler whose read-model changes and checkpoint commit in one transaction.
    /// </summary>
    /// <typeparam name="TProjection">The projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <returns>This projection registration builder.</returns>
    public ProjectionRegistrationBuilder Transactional<TProjection, TEvent>()
        where TProjection : class, IEfProjectionHandler<TEvent>
    {
        eventLoom.AddEfProjection<TProjection, TEvent>(key.Name, key.Version);
        registrationCount++;
        return this;
    }

    /// <summary>
    /// Registers a typed projection handler that executes inside the event append transaction.
    /// </summary>
    /// <typeparam name="TProjection">The inline projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <returns>This projection registration builder.</returns>
    public ProjectionRegistrationBuilder Inline<TProjection, TEvent>()
        where TProjection : class, IInlineProjectionHandler<TEvent>
    {
        eventLoom.AddInlineProjection<TProjection, TEvent>(key.Name, key.Version);
        registrationCount++;
        return this;
    }

    internal void Validate()
    {
        key.Validate();
        if (registrationCount == 0)
        {
            throw new InvalidOperationException(
                $"Projection '{key.Name}' version {key.Version} must register at least one handler.");
        }
    }
}

/// <summary>
/// Adds named projection registration to EventLoom configuration.
/// </summary>
public static class ProjectionRegistrationExtensions
{
    /// <summary>
    /// Registers the handlers for one durable projection name and version.
    /// </summary>
    /// <param name="eventLoom">The EventLoom configuration builder.</param>
    /// <param name="name">The stable projection name and checkpoint namespace.</param>
    /// <param name="configure">Registers asynchronous, transactional, or inline handlers.</param>
    /// <param name="version">The positive projection version.</param>
    /// <returns>The EventLoom configuration builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="eventLoom"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static EventLoomBuilder AddProjection(
        this EventLoomBuilder eventLoom,
        string name,
        Action<ProjectionRegistrationBuilder> configure,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(eventLoom);
        ArgumentNullException.ThrowIfNull(configure);

        var registration = new ProjectionRegistrationBuilder(eventLoom, new ProjectionKey(name, version));
        configure(registration);
        registration.Validate();
        return eventLoom;
    }
}
