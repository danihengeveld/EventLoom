namespace EventLoom.Testing;

/// <summary>Creates Given/When/Then scenarios for event-sourced aggregates.</summary>
public static class AggregateScenario
{
    /// <summary>Starts a scenario for an aggregate constructed from its identifier.</summary>
    /// <typeparam name="TAggregate">The aggregate type under test.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type.</typeparam>
    /// <param name="factory">Creates an aggregate instance for its identifier.</param>
    /// <returns>A new scenario for the aggregate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public static AggregateScenario<TAggregate, TId> For<TAggregate, TId>(Func<TId, TAggregate> factory)
        where TAggregate : Aggregate<TId> =>
        new(factory);
}

/// <summary>
/// Drives an aggregate through a Given/When/Then style test without a database:
/// replay prior history, invoke one command, and assert the resulting state,
/// raised events, or thrown exception.
/// </summary>
/// <typeparam name="TAggregate">The aggregate type under test.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public sealed class AggregateScenario<TAggregate, TId>
    where TAggregate : Aggregate<TId>
{
    private readonly Func<TId, TAggregate> factory;
    private TAggregate? aggregate;
    private Exception? thrownException;

    internal AggregateScenario(Func<TId, TAggregate> factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Gets the aggregate under test after history has been supplied.</summary>
    /// <exception cref="InvalidOperationException">No history has been supplied yet.</exception>
    public TAggregate Aggregate =>
        aggregate ?? throw new InvalidOperationException(
            $"Call {nameof(Given)}(...) before reading the aggregate.");

    /// <summary>Gets the events raised by the most recent command.</summary>
    public IReadOnlyList<object> RaisedEvents =>
        Aggregate.PendingEvents.Select(pending => pending.Event).ToArray();

    /// <summary>Gets the exception thrown by the most recent <see cref="When"/> call, if any.</summary>
    public Exception? ThrownException => thrownException;

    /// <summary>
    /// Constructs the aggregate and replays its prior history as already-persisted events.
    /// </summary>
    /// <param name="id">The aggregate identifier.</param>
    /// <param name="history">The prior events to replay, in stream order.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate, TId> Given(TId id, params object[] history)
    {
        ArgumentNullException.ThrowIfNull(history);
        aggregate = factory(id);
        aggregate.ApplyHistory(history);
        return this;
    }

    /// <summary>
    /// Constructs the aggregate and replays its prior history as already-persisted events.
    /// </summary>
    /// <param name="id">The aggregate identifier.</param>
    /// <param name="history">The prior events to replay, in stream order.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate, TId> Given(TId id, IEnumerable<object> history) =>
        Given(id, (history ?? throw new ArgumentNullException(nameof(history))).ToArray());

    /// <summary>
    /// Invokes one command against the aggregate. Any thrown exception is captured
    /// for <see cref="ThrownException"/> instead of propagating.
    /// </summary>
    /// <param name="command">The command to invoke on the aggregate.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public AggregateScenario<TAggregate, TId> When(Action<TAggregate> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        thrownException = null;
        try
        {
            command(Aggregate);
        }
        catch (Exception exception)
        {
            thrownException = exception;
        }

        return this;
    }

    /// <summary>Asserts against the current aggregate state.</summary>
    /// <param name="assertion">Invoked with the aggregate under test.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate, TId> Then(Action<TAggregate> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertion(Aggregate);
        return this;
    }

    /// <summary>Asserts against the events raised by the most recent <see cref="When"/> call.</summary>
    /// <param name="assertion">Invoked with the raised events.</param>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate, TId> ThenEvents(Action<IReadOnlyList<object>> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertion(RaisedEvents);
        return this;
    }

    /// <summary>Asserts that the most recent <see cref="When"/> call raised no events.</summary>
    /// <returns>This scenario.</returns>
    public AggregateScenario<TAggregate, TId> ThenNoEventsRaised() =>
        ThenEvents(events =>
        {
            if (events.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Expected no raised events, but {events.Count} event(s) were raised.");
            }
        });

    /// <summary>Asserts that the most recent <see cref="When"/> call threw <typeparamref name="TException"/>.</summary>
    /// <typeparam name="TException">The expected exception type.</typeparam>
    /// <returns>This scenario.</returns>
    /// <exception cref="InvalidOperationException">
    /// No exception was thrown, or a different exception type was thrown.
    /// </exception>
    public AggregateScenario<TAggregate, TId> ThenThrows<TException>()
        where TException : Exception
    {
        if (thrownException is null)
        {
            throw new InvalidOperationException(
                $"Expected a {typeof(TException).Name} to be thrown, but no exception was thrown.");
        }

        if (thrownException is not TException)
        {
            throw new InvalidOperationException(
                $"Expected a {typeof(TException).Name} to be thrown, but a {thrownException.GetType().Name} was thrown.",
                thrownException);
        }

        return this;
    }
}
