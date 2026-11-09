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
        where TAggregate : Aggregate<TAggregate, TId> =>
        new(factory);
}

/// <summary>
/// Drives an aggregate through a Given/When/Then style test without a database:
/// replay prior history, invoke one command, and assert the resulting state,
/// raised events, or thrown exception.
/// </summary>
/// <remarks>
/// <para>
/// An exception thrown by <see cref="When"/> is captured. Assert it with
/// <see cref="ThenThrows{TException}"/> before any other <c>Then</c> assertion; every other
/// <c>Then</c> method fails while a captured exception is unobserved, so an unexpected
/// failure cannot make a test pass.
/// </para>
/// <para>
/// Use <see cref="ThenAsync"/> and <see cref="ThenEventsAsync"/> with assertion libraries whose
/// assertions must be awaited, such as TUnit. The synchronous <see cref="Then"/> and
/// <see cref="ThenEvents(Action{IReadOnlyList{object}})"/> only observe assertions that throw.
/// </para>
/// </remarks>
/// <typeparam name="TAggregate">The aggregate type under test.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public sealed class AggregateScenario<TAggregate, TId>
    where TAggregate : Aggregate<TAggregate, TId>
{
    private readonly Func<TId, TAggregate> factory;
    private TAggregate? aggregate;
    private Exception? thrownException;
    private bool thrownExceptionObserved;

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
    /// <exception cref="InvalidOperationException">The aggregate factory returned <see langword="null"/>.</exception>
    /// <exception cref="AggregateFactoryException">
    /// The aggregate factory returned an aggregate that already has applied or pending events.
    /// </exception>
    /// <exception cref="EventOwnershipException">The history contains an event the aggregate does not own.</exception>
    public AggregateScenario<TAggregate, TId> Given(TId id, params object[] history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var created = factory(id) ?? throw new InvalidOperationException("The aggregate factory returned null.");
        if (!created.IsPristine)
        {
            throw new AggregateFactoryException(typeof(TAggregate));
        }

        aggregate = created;
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
        thrownExceptionObserved = false;
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

    /// <summary>Asserts against the current aggregate state with an assertion that throws on failure.</summary>
    /// <param name="assertion">Invoked with the aggregate under test.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="InvalidOperationException">The most recent command threw an exception that was not asserted.</exception>
    public AggregateScenario<TAggregate, TId> Then(Action<TAggregate> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        EnsureNoUnobservedException();
        assertion(Aggregate);
        return this;
    }

    /// <summary>Asserts against the current aggregate state with an awaited assertion.</summary>
    /// <param name="assertion">Invoked with the aggregate under test and awaited.</param>
    /// <returns>This scenario, once the assertion has completed.</returns>
    /// <exception cref="InvalidOperationException">The most recent command threw an exception that was not asserted.</exception>
    /// <example>
    /// <code>
    /// await scenario.ThenAsync(async order => await Assert.That(order.Status).IsEqualTo(OrderStatus.Cancelled));
    /// </code>
    /// </example>
    public async Task<AggregateScenario<TAggregate, TId>> ThenAsync(Func<TAggregate, Task> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        EnsureNoUnobservedException();
        await assertion(Aggregate).ConfigureAwait(false);
        return this;
    }

    /// <summary>
    /// Asserts against the events raised by the most recent <see cref="When"/> call with an assertion that throws
    /// on failure.
    /// </summary>
    /// <param name="assertion">Invoked with the raised events.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="InvalidOperationException">The most recent command threw an exception that was not asserted.</exception>
    public AggregateScenario<TAggregate, TId> ThenEvents(Action<IReadOnlyList<object>> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        EnsureNoUnobservedException();
        assertion(RaisedEvents);
        return this;
    }

    /// <summary>
    /// Asserts against the events raised by the most recent <see cref="When"/> call with an awaited assertion.
    /// </summary>
    /// <param name="assertion">Invoked with the raised events and awaited.</param>
    /// <returns>This scenario, once the assertion has completed.</returns>
    /// <exception cref="InvalidOperationException">The most recent command threw an exception that was not asserted.</exception>
    public async Task<AggregateScenario<TAggregate, TId>> ThenEventsAsync(
        Func<IReadOnlyList<object>, Task> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        EnsureNoUnobservedException();
        await assertion(RaisedEvents).ConfigureAwait(false);
        return this;
    }

    /// <summary>
    /// Asserts that the most recent <see cref="When"/> call raised exactly the expected events, in order, compared
    /// with <see cref="object.Equals(object?)"/>. Records compare by value.
    /// </summary>
    /// <param name="expected">The events the command should have raised, in order.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="InvalidOperationException">
    /// The raised events differ from <paramref name="expected"/>, or the most recent command threw an exception
    /// that was not asserted.
    /// </exception>
    public AggregateScenario<TAggregate, TId> ThenRaised(params object[] expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureNoUnobservedException();
        var raised = RaisedEvents;
        if (raised.Count != expected.Length || !raised.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Expected raised events [{Describe(expected)}], but the command raised [{Describe(raised)}].");
        }

        return this;
    }

    /// <summary>Asserts that the most recent <see cref="When"/> call raised no events.</summary>
    /// <returns>This scenario.</returns>
    /// <exception cref="InvalidOperationException">
    /// Events were raised, or the most recent command threw an exception that was not asserted.
    /// </exception>
    public AggregateScenario<TAggregate, TId> ThenNoEventsRaised() =>
        ThenEvents(events =>
        {
            if (events.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Expected no raised events, but {events.Count} event(s) were raised: [{Describe(events)}].");
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

        thrownExceptionObserved = true;
        return this;
    }

    private void EnsureNoUnobservedException()
    {
        if (thrownException is not null && !thrownExceptionObserved)
        {
            throw new InvalidOperationException(
                $"The command passed to {nameof(When)}(...) threw an unexpected " +
                $"{thrownException.GetType().Name}: {thrownException.Message} If the exception is expected, " +
                $"assert it with {nameof(ThenThrows)}<{thrownException.GetType().Name}>() before other Then assertions.",
                thrownException);
        }
    }

    private static string Describe(IEnumerable<object> events) =>
        string.Join(", ", events.Select(value => value?.ToString() ?? "null"));
}
