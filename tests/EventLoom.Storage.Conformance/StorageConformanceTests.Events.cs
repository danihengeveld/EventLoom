using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

/// <summary>
/// The behavior every EventLoom storage provider must satisfy. Provider test projects derive from this class,
/// supply an isolated <see cref="StorageEnvironment"/>, and inherit every test.
/// </summary>
public abstract partial class StorageConformanceTests
{
    private const string Tenant = "tenant-a";

    /// <summary>Creates a fresh, empty storage environment for one test.</summary>
    protected abstract Task<StorageEnvironment> CreateEnvironmentAsync();

    [Test]
    public async Task Append_assigns_consecutive_versions_and_gapless_tenant_offsets()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();

        var first = await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 2), null);
        var second = await events.AppendAsync(Request("order-2", ExpectedVersion.NoStream, 1), null);
        var third = await events.AppendAsync(Request("order-1", ExpectedVersion.Exact(2), 1), null);

        await Assert.That(first.WasIdempotentReplay).IsFalse();
        await Assert.That(first.Events.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(first.Events.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(second.Events.Single().StreamVersion).IsEqualTo(1);
        await Assert.That(second.Events.Single().TenantOffset).IsEqualTo(3);
        await Assert.That(third.Events.Single().StreamVersion).IsEqualTo(3);
        await Assert.That(third.Events.Single().TenantOffset).IsEqualTo(4);
    }

    [Test]
    public async Task Persisted_events_round_trip_every_field()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var eventId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2025, 6, 1, 12, 30, 15, 250, TimeSpan.Zero);
        var metadata = new EventMetadata(
            "correlation-1",
            "causation-1",
            "actor-1",
            new Dictionary<string, string> { ["region"] = "eu", ["a.b"] = "c" });

        await events.AppendAsync(
            Request("order-1", ExpectedVersion.NoStream, 0) with
            {
                Events = [new NewStoredEvent(eventId, "tests.added", 3, """{"value":"é\"x"}""", occurredAt)],
                Metadata = metadata
            },
            null);

        var stored = (await events.ReadStreamAsync(Tenant, "order-1", null, null)).Single();
        await Assert.That(stored.EventId).IsEqualTo(eventId);
        await Assert.That(stored.TenantId).IsEqualTo(Tenant);
        await Assert.That(stored.StreamId).IsEqualTo("order-1");
        await Assert.That(stored.AggregateType).IsEqualTo("order");
        await Assert.That(stored.EventType).IsEqualTo("tests.added");
        await Assert.That(stored.EventTypeVersion).IsEqualTo(3);
        await Assert.That(stored.Payload).IsEqualTo("""{"value":"é\"x"}""");
        await Assert.That(stored.OccurredAt).IsEqualTo(occurredAt);
        await Assert.That(stored.Metadata.CorrelationId).IsEqualTo("correlation-1");
        await Assert.That(stored.Metadata.CausationId).IsEqualTo("causation-1");
        await Assert.That(stored.Metadata.Actor).IsEqualTo("actor-1");
        await Assert.That(stored.Metadata.Headers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))
            .IsEquivalentTo(new[] { "a.b=c", "region=eu" });
    }

    [Test]
    public async Task Events_without_metadata_are_read_back_with_empty_headers()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), null);

        var stored = (await events.ReadStreamAsync(Tenant, "order-1", null, null)).Single();

        await Assert.That(stored.Metadata.Headers.Count).IsEqualTo(0);
        await Assert.That(stored.Metadata.CorrelationId).IsNull();
        await Assert.That(stored.Metadata.CausationId).IsNull();
        await Assert.That(stored.Metadata.Actor).IsNull();
    }

    [Test]
    public async Task Stream_reads_are_ordered_and_bounded_by_version()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 5), null);

        var all = await events.ReadStreamAsync(Tenant, "order-1", null, null);
        var from = await events.ReadStreamAsync(Tenant, "order-1", 3, null);
        var to = await events.ReadStreamAsync(Tenant, "order-1", null, 2);
        var window = await events.ReadStreamAsync(Tenant, "order-1", 2, 4);
        var missing = await events.ReadStreamAsync(Tenant, "missing", null, null);

        await Assert.That(all.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 1, 2, 3, 4, 5 });
        await Assert.That(from.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 3, 4, 5 });
        await Assert.That(to.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(window.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 2, 3, 4 });
        await Assert.That(missing.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Tenant_reads_follow_offsets_after_a_position_up_to_a_limit()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 2), null);
        await events.AppendAsync(Request("order-2", ExpectedVersion.NoStream, 2), null);

        var afterOne = await events.ReadTenantAsync(Tenant, 1, 10);
        var limited = await events.ReadTenantAsync(Tenant, 0, 3);
        var beyond = await events.ReadTenantAsync(Tenant, 4, 10);

        await Assert.That(afterOne.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 2, 3, 4 });
        await Assert.That(limited.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 1, 2, 3 });
        await Assert.That(beyond.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Tenants_are_isolated_even_when_stream_ids_collide()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();

        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 2), null);
        var other = await events.AppendAsync(
            Request("order-1", ExpectedVersion.NoStream, 1) with { TenantId = "tenant-b" }, null);

        await Assert.That(other.Events.Single().StreamVersion).IsEqualTo(1);
        await Assert.That(other.Events.Single().TenantOffset).IsEqualTo(1);
        await Assert.That((await events.ReadStreamAsync(Tenant, "order-1", null, null)).Count).IsEqualTo(2);
        await Assert.That((await events.ReadStreamAsync("tenant-b", "order-1", null, null)).Count).IsEqualTo(1);
        await Assert.That((await events.ReadTenantAsync("tenant-b", 0, 10)).Count).IsEqualTo(1);
        await Assert.That(await events.ReadStreamAsync("tenant-c", "order-1", null, null)).IsEmpty();
    }

    [Test]
    public async Task Wrong_expected_version_is_rejected_and_leaves_no_trace()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), null);

        await Assert.That(async () => await events.AppendAsync(
                Request("order-1", ExpectedVersion.NoStream, 2), null))
            .Throws<WrongExpectedVersionException>();
        await Assert.That(async () => await events.AppendAsync(
                Request("order-1", ExpectedVersion.Exact(5), 2), null))
            .Throws<WrongExpectedVersionException>();
        await Assert.That(async () => await events.AppendAsync(
                Request("missing", ExpectedVersion.StreamExists, 1), null))
            .Throws<WrongExpectedVersionException>();
        await Assert.That(async () => await events.AppendAsync(
                Request("missing", ExpectedVersion.Exact(1), 1), null))
            .Throws<WrongExpectedVersionException>();

        var next = await events.AppendAsync(Request("order-2", ExpectedVersion.NoStream, 1), null);
        await Assert.That((await events.ReadStreamAsync(Tenant, "order-1", null, null)).Count).IsEqualTo(1);
        await Assert.That(next.Events.Single().TenantOffset).IsEqualTo(2);
    }

    [Test]
    public async Task Exact_any_and_stream_exists_expectations_are_honored()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();

        await events.AppendAsync(Request("order-1", ExpectedVersion.Any, 1), null);
        await events.AppendAsync(Request("order-1", ExpectedVersion.StreamExists, 1), null);
        await events.AppendAsync(Request("order-1", ExpectedVersion.Exact(2), 1), null);
        await events.AppendAsync(Request("order-1", ExpectedVersion.Any, 1), null);

        var stored = await events.ReadStreamAsync(Tenant, "order-1", null, null);
        await Assert.That(stored.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task Append_id_replays_the_original_result_without_new_events()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var request = Request("order-1", ExpectedVersion.NoStream, 2) with { AppendId = "append-1" };

        var first = await events.AppendAsync(request, null);
        var replay = await events.AppendAsync(request, null);

        await Assert.That(first.WasIdempotentReplay).IsFalse();
        await Assert.That(replay.WasIdempotentReplay).IsTrue();
        await Assert.That(replay.Events.Select(value => value.EventId))
            .IsEquivalentTo(first.Events.Select(value => value.EventId));
        await Assert.That((await events.ReadStreamAsync(Tenant, "order-1", null, null)).Count).IsEqualTo(2);
        await Assert.That((await events.ReadTenantAsync(Tenant, 0, 10)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Append_ids_are_scoped_to_their_tenant()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();

        var first = await events.AppendAsync(
            Request("order-1", ExpectedVersion.NoStream, 1) with { AppendId = "shared" }, null);
        var otherTenant = await events.AppendAsync(
            Request("order-1", ExpectedVersion.NoStream, 1) with { AppendId = "shared", TenantId = "tenant-b" }, null);

        await Assert.That(first.WasIdempotentReplay).IsFalse();
        await Assert.That(otherTenant.WasIdempotentReplay).IsFalse();
        await Assert.That((await events.ReadStreamAsync("tenant-b", "order-1", null, null)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Outbox_messages_are_written_atomically_only_when_requested()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();

        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), null);
        var withOutbox = await events.AppendAsync(
            Request("order-2", ExpectedVersion.NoStream, 2) with { WriteOutbox = true }, null);

        var pending = await outbox.ReadPendingAsync(Tenant, 10);
        await Assert.That(pending.Select(value => value.MessageId))
            .IsEquivalentTo(withOutbox.Events.Select(value => value.EventId));
        await Assert.That(pending.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 2, 3 });
        await Assert.That(pending.All(value => value.AttemptCount == 0 && value.PublishedAt is null)).IsTrue();
    }

    [Test]
    public async Task Failing_before_commit_callback_rolls_back_the_append()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        IReadOnlyList<StoredEvent>? observed = null;

        await Assert.That(async () => await events.AppendAsync(
                Request("order-1", ExpectedVersion.NoStream, 2) with
                {
                    WriteOutbox = true,
                    BeforeCommit = (stored, _) =>
                    {
                        observed = stored;
                        throw new InvalidOperationException("inline projection failed");
                    }
                },
                null))
            .Throws<InvalidOperationException>();

        await Assert.That(observed!.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(await events.ReadStreamAsync(Tenant, "order-1", null, null)).IsEmpty();
        await Assert.That(await outbox.ReadPendingAsync(Tenant, 10)).IsEmpty();
        var next = await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), null);
        await Assert.That(next.Events.Single().TenantOffset).IsEqualTo(1);
    }

    [Test]
    public async Task Transactions_commit_or_discard_every_append_together()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();

        await using (var rolledBack = await events.BeginTransactionAsync())
        {
            await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1) with { WriteOutbox = true },
                rolledBack);
            await events.AppendAsync(Request("order-2", ExpectedVersion.NoStream, 1) with { WriteOutbox = true },
                rolledBack);
            await rolledBack.RollbackAsync();
        }

        await Assert.That(await events.ReadTenantAsync(Tenant, 0, 10)).IsEmpty();
        await Assert.That(await outbox.ReadPendingAsync(Tenant, 10)).IsEmpty();

        await using (var committed = await events.BeginTransactionAsync())
        {
            await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1) with { WriteOutbox = true },
                committed);
            await events.AppendAsync(Request("order-1", ExpectedVersion.Exact(1), 1) with { WriteOutbox = true },
                committed);
            await committed.CommitAsync();
        }

        var stored = await events.ReadTenantAsync(Tenant, 0, 10);
        await Assert.That(stored.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That((await outbox.ReadPendingAsync(Tenant, 10)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Disposing_a_transaction_without_committing_discards_its_appends()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();

        await using (var abandoned = await events.BeginTransactionAsync())
        {
            await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), abandoned);
        }

        await Assert.That(await events.ReadTenantAsync(Tenant, 0, 10)).IsEmpty();
    }

    [Test]
    public async Task Tenant_heads_report_the_last_committed_offset_per_tenant()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 3), null);
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1) with { TenantId = "tenant-b" }, null);
        await Assert.That(async () => await events.AppendAsync(
                Request("order-1", ExpectedVersion.NoStream, 1) with { TenantId = "tenant-c" } with
                {
                    BeforeCommit = (_, _) => throw new InvalidOperationException("rollback")
                },
                null))
            .Throws<InvalidOperationException>();

        var heads = await events.ReadTenantHeadsAsync();

        await Assert
            .That(heads.OrderBy(value => value.TenantId).Select(value => $"{value.TenantId}:{value.LastOffset}"))
            .IsEquivalentTo(new[] { "tenant-a:3", "tenant-b:1" });
    }

    [Test]
    public async Task Concurrent_first_appends_to_one_stream_produce_exactly_one_winner()
    {
        await using var environment = await CreateEnvironmentAsync();
        if (!environment.Capabilities.IsDistributed)
        {
            Skip.Test(
                $"{environment.Capabilities.ProviderName} is single-node; concurrency is validated by distributed providers.");
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = environment.CreateScope();
            try
            {
                await AppendTransientSafeAsync(scope.ServiceProvider, Request("order-1", ExpectedVersion.NoStream, 1));
                return true;
            }
            catch (Exception exception) when (
                exception is WrongExpectedVersionException or EventStoreConcurrencyException)
            {
                return false;
            }
        }));

        await using var reader = environment.CreateScope();
        var stored = await reader.ServiceProvider.GetRequiredService<IEventStorage>()
            .ReadStreamAsync(Tenant, "order-1", null, null);
        await Assert.That(outcomes.Count(value => value)).IsEqualTo(1);
        await Assert.That(stored.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_appends_to_one_tenant_commit_gapless_offsets()
    {
        await using var environment = await CreateEnvironmentAsync();
        if (!environment.Capabilities.IsDistributed)
        {
            Skip.Test(
                $"{environment.Capabilities.ProviderName} is single-node; concurrency is validated by distributed providers.");
        }

        const int Writers = 6;
        const int AppendsPerWriter = 8;
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(async writer =>
        {
            await using var scope = environment.CreateScope();
            for (var index = 0; index < AppendsPerWriter; index++)
            {
                await AppendWithRetryAsync(scope.ServiceProvider, Request($"order-{writer}", ExpectedVersion.Any, 1));
            }
        }));

        await using var reader = environment.CreateScope();
        var all = await reader.ServiceProvider.GetRequiredService<IEventStorage>().ReadTenantAsync(Tenant, 0, 1000);
        await Assert.That(all.Select(value => value.TenantOffset))
            .IsEquivalentTo(Enumerable.Range(1, Writers * AppendsPerWriter).Select(value => (long)value));
        foreach (var stream in all.GroupBy(value => value.StreamId))
        {
            await Assert.That(stream.OrderBy(value => value.TenantOffset).Select(value => value.StreamVersion))
                .IsEquivalentTo(Enumerable.Range(1, AppendsPerWriter).Select(value => (long)value));
        }
    }

    [Test]
    public async Task Concurrent_appends_with_one_append_id_replay_a_single_result()
    {
        await using var environment = await CreateEnvironmentAsync();
        if (!environment.Capabilities.IsDistributed)
        {
            Skip.Test(
                $"{environment.Capabilities.ProviderName} is single-node; concurrency is validated by distributed providers.");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = environment.CreateScope();
            return await AppendWithRetryAsync(
                scope.ServiceProvider,
                Request("order-1", ExpectedVersion.NoStream, 1) with { AppendId = "append-1" });
        }));

        await using var reader = environment.CreateScope();
        var stored = await reader.ServiceProvider.GetRequiredService<IEventStorage>()
            .ReadStreamAsync(Tenant, "order-1", null, null);
        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(results.Count(value => !value.WasIdempotentReplay)).IsEqualTo(1);
        await Assert.That(results.Select(value => value.Events.Single().EventId).Distinct().Count()).IsEqualTo(1);
    }

    // Transient provider failures are retried by the engine's retry policy, so concurrency tests apply it too.
    private static Task<StorageAppendResult> AppendTransientSafeAsync(
        IServiceProvider services,
        StorageAppendRequest request)
    {
        var events = services.GetRequiredService<IEventStorage>();
        var policy = services.GetService<IEventStoreRetryPolicy>();
        return policy is null
            ? events.AppendAsync(request, null)
            : policy.ExecuteAsync(token => events.AppendAsync(request, null, token));
    }

    private static async Task<StorageAppendResult> AppendWithRetryAsync(IServiceProvider services,
        StorageAppendRequest request)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await AppendTransientSafeAsync(services, request);
            }
            catch (EventStoreConcurrencyException) when (attempt < 50)
            {
                await Task.Delay(5);
            }
        }
    }

    private static StorageAppendRequest Request(
        string streamId,
        ExpectedVersion expected,
        int count,
        string tenantId = Tenant) =>
        new(
            tenantId,
            streamId,
            "order",
            expected,
            Enumerable.Range(0, count)
                .Select(index => new NewStoredEvent(
                    Guid.NewGuid(),
                    "tests.added",
                    1,
                    $$"""{"index":{{index}}}""",
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
                .ToArray(),
            new EventMetadata(),
            null,
            false);
}
