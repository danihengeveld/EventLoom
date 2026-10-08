using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

public abstract partial class StorageConformanceTests
{
    private const string OutboxLease = "outbox:publisher";

    [Test]
    public async Task Pending_messages_are_listed_in_tenant_offset_order_per_tenant()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 2) with { WriteOutbox = true }, null);
        await events.AppendAsync(
            Request("order-1", ExpectedVersion.NoStream, 1) with { WriteOutbox = true, TenantId = "tenant-b" }, null);
        await events.AppendAsync(Request("order-2", ExpectedVersion.NoStream, 1) with { WriteOutbox = true }, null);

        var pending = await outbox.ReadPendingAsync(Tenant, 10);
        var limited = await outbox.ReadPendingAsync(Tenant, 2);

        await Assert.That(pending.Select(value => value.TenantOffset)).IsEquivalentTo(new long[] { 1, 2, 3 });
        await Assert.That(limited.Count).IsEqualTo(2);
        await Assert.That(await outbox.CountPendingAsync()).IsEqualTo(4);
        await Assert.That(await outbox.ReadPendingTenantIdsAsync()).IsEquivalentTo(new[] { "tenant-a", "tenant-b" });
        var first = pending.First();
        await Assert.That(first.StreamId).IsEqualTo("order-1");
        await Assert.That(first.AggregateType).IsEqualTo("order");
        await Assert.That(first.EventType).IsEqualTo("tests.added");
        await Assert.That(first.Payload).IsEqualTo("""{"index":0}""");
        await Assert.That((await outbox.GetAsync(Tenant, first.MessageId))!.MessageId).IsEqualTo(first.MessageId);
        await Assert.That(await outbox.GetAsync("tenant-b", first.MessageId)).IsNull();
        await Assert.That(await outbox.GetAsync(Tenant, Guid.NewGuid())).IsNull();
    }

    [Test]
    public async Task A_successful_attempt_publishes_the_message_and_records_the_attempt()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (message, lease) = await ArrangeOutboxAsync(scope.ServiceProvider);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();

        var recorded = await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.FromDays(1));

        var stored = (await outbox.GetAsync(Tenant, message.MessageId))!;
        var attempts = await outbox.ReadAttemptsAsync(Tenant, message.MessageId);
        await Assert.That(recorded).IsTrue();
        await Assert.That(stored.PublishedAt).IsEqualTo(environment.Clock.GetUtcNow());
        await Assert.That(stored.AttemptCount).IsEqualTo(1);
        await Assert.That(await outbox.ReadPendingAsync(Tenant, 10)).IsEmpty();
        await Assert.That(await outbox.CountPendingAsync()).IsEqualTo(0);
        var attempt = attempts.Single();
        await Assert.That(attempt.Succeeded).IsTrue();
        await Assert.That(attempt.AttemptNumber).IsEqualTo(1);
        await Assert.That(attempt.ExceptionType).IsNull();
        await Assert.That(await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.FromDays(1))).IsFalse();
    }

    [Test]
    public async Task Attempts_are_numbered_and_record_only_the_exception_type()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (message, lease) = await ArrangeOutboxAsync(scope.ServiceProvider);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();

        await outbox.RecordAttemptAsync(message, lease, "System.TimeoutException", TimeSpan.FromDays(1));
        await outbox.RecordAttemptAsync(message, lease, "System.TimeoutException", TimeSpan.FromDays(1));
        await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.FromDays(1));

        var attempts = await outbox.ReadAttemptsAsync(Tenant, message.MessageId);
        await Assert.That(attempts.Select(value => value.AttemptNumber)).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That(attempts.Select(value => value.Succeeded)).IsEquivalentTo(new[] { false, false, true });
        await Assert.That(attempts.First().ExceptionType).IsEqualTo("System.TimeoutException");
        await Assert.That((await outbox.GetAsync(Tenant, message.MessageId))!.AttemptCount).IsEqualTo(3);
    }

    [Test]
    public async Task A_failed_attempt_leaves_the_message_pending()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (message, lease) = await ArrangeOutboxAsync(scope.ServiceProvider);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();

        await outbox.RecordAttemptAsync(message, lease, "System.TimeoutException", TimeSpan.FromDays(1));

        var pending = await outbox.ReadPendingAsync(Tenant, 10);
        await Assert.That(pending.Single().AttemptCount).IsEqualTo(1);
        await Assert.That(pending.Single().PublishedAt).IsNull();
    }

    [Test]
    public async Task Zero_retention_removes_a_delivered_message_and_its_attempts()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (message, lease) = await ArrangeOutboxAsync(scope.ServiceProvider);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        await outbox.RecordAttemptAsync(message, lease, "System.TimeoutException", TimeSpan.Zero);

        await Assert.That(await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.Zero)).IsTrue();

        await Assert.That(await outbox.GetAsync(Tenant, message.MessageId)).IsNull();
        await Assert.That(await outbox.ReadAttemptsAsync(Tenant, message.MessageId)).IsEmpty();
    }

    [Test]
    public async Task A_stale_lease_cannot_record_a_delivery()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (message, lease) = await ArrangeOutboxAsync(scope.ServiceProvider);
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        environment.Clock.Advance(LeaseDuration);
        await leases.TryAcquireAsync(Tenant, OutboxLease, "owner-2", LeaseDuration);

        await Assert.That(async () => await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.FromDays(1)))
            .Throws<OutboxLeaseLostException>();

        await Assert.That((await outbox.GetAsync(Tenant, message.MessageId))!.PublishedAt).IsNull();
        await Assert.That(await outbox.ReadAttemptsAsync(Tenant, message.MessageId)).IsEmpty();
    }

    [Test]
    public async Task Purge_removes_only_delivered_messages_older_than_the_retention()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 3) with { WriteOutbox = true }, null);
        var messages = await outbox.ReadPendingAsync(Tenant, 10);
        var lease = (await leases.TryAcquireAsync(Tenant, OutboxLease, "owner-1", TimeSpan.FromDays(2)))!;
        await outbox.RecordAttemptAsync(messages[0], lease, null, TimeSpan.FromDays(1));
        environment.Clock.Advance(TimeSpan.FromHours(30));
        await outbox.RecordAttemptAsync(messages[1], lease, null, TimeSpan.FromDays(1));

        var purged = await outbox.PurgePublishedAsync(TimeSpan.FromDays(1), 100);

        await Assert.That(purged).IsEqualTo(1);
        await Assert.That(await outbox.GetAsync(Tenant, messages[0].MessageId)).IsNull();
        await Assert.That(await outbox.ReadAttemptsAsync(Tenant, messages[0].MessageId)).IsEmpty();
        await Assert.That(await outbox.GetAsync(Tenant, messages[1].MessageId)).IsNotNull();
        await Assert.That(await outbox.GetAsync(Tenant, messages[2].MessageId)).IsNotNull();
        await Assert.That(await outbox.PurgePublishedAsync(TimeSpan.FromDays(1), 100)).IsEqualTo(0);
    }

    [Test]
    public async Task Purge_respects_the_batch_limit()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStorage>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 3) with { WriteOutbox = true }, null);
        var lease = (await leases.TryAcquireAsync(Tenant, OutboxLease, "owner-1", TimeSpan.FromDays(2)))!;
        foreach (var message in await outbox.ReadPendingAsync(Tenant, 10))
        {
            await outbox.RecordAttemptAsync(message, lease, null, TimeSpan.FromDays(1));
        }

        environment.Clock.Advance(TimeSpan.FromDays(2));

        await Assert.That(await outbox.PurgePublishedAsync(TimeSpan.FromDays(1), 2)).IsEqualTo(2);
        await Assert.That(await outbox.PurgePublishedAsync(TimeSpan.FromDays(1), 2)).IsEqualTo(1);
    }

    private static async Task<(OutboxMessage Message, WorkerLease Lease)> ArrangeOutboxAsync(IServiceProvider services)
    {
        var events = services.GetRequiredService<IEventStorage>();
        await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1) with { WriteOutbox = true }, null);
        var message = (await services.GetRequiredService<IOutboxStorage>().ReadPendingAsync(Tenant, 1)).Single();
        var lease = await services.GetRequiredService<IWorkerLeaseStorage>()
            .TryAcquireAsync(Tenant, OutboxLease, "owner-1", LeaseDuration);
        return (message, lease!);
    }
}
