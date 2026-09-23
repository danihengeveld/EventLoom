using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class ConfigureAwaitTests
{
    [Test]
    public async Task Unit_of_work_disposal_does_not_post_to_callers_synchronization_context()
    {
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite("Data Source=:memory:").Options,
            new EventStoreOptions());
        var store = new EventStore(
            context, new EventSerializer(new EventRegistry()), new UuidV7EventIdGenerator(), TimeProvider.System);
        var transaction = new DeferredTransaction();
        var unitOfWork = new EventLoomUnitOfWork(store, context, transaction);
        var synchronizationContext = new CountingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task disposeTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            disposeTask = unitOfWork.DisposeAsync().AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        transaction.CompleteRollback();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(synchronizationContext.PostCount).IsEqualTo(0);
        await Assert.That(transaction.WasDisposed).IsTrue();
    }

    [Test]
    public async Task Inline_dispatch_preserves_context_between_application_handlers()
    {
        var key = new ProjectionKey("tests.callbacks", 1);
        SynchronizationContext? secondHandlerContext = null;
        var registry = new ProjectionRegistry([
            new ProjectionHandlerRegistration(
                key, ProjectionMode.Inline, typeof(object), typeof(string),
                (_, _, _, token) => Task.Delay(TimeSpan.FromMilliseconds(10), token)),
            new ProjectionHandlerRegistration(
                key, ProjectionMode.Inline, typeof(object), typeof(int),
                (_, _, _, _) =>
                {
                    secondHandlerContext = SynchronizationContext.Current;
                    return Task.CompletedTask;
                })
        ]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var envelope = new EventEnvelope(
            Guid.NewGuid(), "tests.callbacks", 1, "stream", "test", 1, 1,
            new TenantId("tenant-a"), DateTimeOffset.UtcNow, new object(), new EventMetadata());
        var synchronizationContext = new PostingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task dispatchTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            dispatchTask = registry.DispatchInlineAsync(services, envelope, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(ReferenceEquals(secondHandlerContext, synchronizationContext)).IsTrue();
    }

    private sealed class PostingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                try
                {
                    SetSynchronizationContext(this);
                    callback(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int postCount;

        public int PostCount => Volatile.Read(ref postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref postCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class DeferredTransaction : IDbContextTransaction
    {
        private readonly TaskCompletionSource rollback = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid TransactionId { get; } = Guid.NewGuid();

        public bool WasDisposed { get; private set; }

        public void CompleteRollback() => rollback.SetResult();

        public void Commit() => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Rollback() => throw new NotSupportedException();

        public Task RollbackAsync(CancellationToken cancellationToken = default) => rollback.Task;

        public void Dispose() => WasDisposed = true;

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
