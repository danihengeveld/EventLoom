using EventLoom.EntityFrameworkCore;

namespace EventLoom.Hosting;

/// <summary>Provides aggregate-only operational diagnostics for EventLoom workers.</summary>
public sealed class EventLoomOperationalDiagnostics
{
    private readonly ProjectionStore projections;
    private readonly OutboxStore outbox;
    private readonly ProjectionRegistry registry;

    internal EventLoomOperationalDiagnostics(
        ProjectionStore projections,
        OutboxStore outbox,
        ProjectionRegistry registry)
    {
        this.projections = projections ?? throw new ArgumentNullException(nameof(projections));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Gets payload-safe worker diagnostic summaries.</summary>
    public async Task<EventLoomOperationalSummary> GetAsync(CancellationToken cancellationToken = default) =>
        new(
            await projections.GetHealthSummaryAsync(registry.AsynchronousProjections, cancellationToken)
                .ConfigureAwait(false),
            await outbox.GetHealthSummaryAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary>Contains payload-safe aggregate worker diagnostics.</summary>
public sealed record EventLoomOperationalSummary(
    ProjectionHealthSummary Projections,
    OutboxHealthSummary Outbox);
