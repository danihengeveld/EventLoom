namespace EventLoom.Ordering.Api.Domain;

[EventType("ordering.order-placed", Version = 1)]
internal sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent;

[EventType("ordering.order-item-added", Version = 1)]
internal sealed record OrderItemAdded(string Sku, int Quantity) : IDomainEvent;

[EventType("ordering.order-cancelled", Version = 1)]
internal sealed record OrderCancelled(string Reason) : IDomainEvent;

internal sealed record OrderItem(string Sku, int Quantity);

[SnapshotType("ordering.order", Version = 1)]
internal sealed record OrderSnapshot(string Status, IReadOnlyList<OrderItem> Items) : IAggregateSnapshot;

internal sealed class Order(Guid id) : Aggregate<Guid>(id)
{
    private readonly List<OrderItem> _items = [];

    public IReadOnlyList<OrderItem> Items => _items;
    public string Status { get; private set; } = "new";

    public void Place(string sku, int quantity)
    {
        EnsureValidItem(sku, quantity);
        if (Version != 0)
        {
            throw new InvalidOperationException("An order can only be placed once.");
        }

        Raise(new OrderPlaced(sku, quantity));
    }

    public void AddItem(string sku, int quantity)
    {
        EnsureValidItem(sku, quantity);
        EnsureActive();
        Raise(new OrderItemAdded(sku, quantity));
    }

    public void Cancel(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureActive();
        Raise(new OrderCancelled(reason));
    }

    public void Restore(OrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _items.Clear();
        _items.AddRange(snapshot.Items);
        Status = snapshot.Status;
    }

    private void Apply(OrderPlaced @event)
    {
        _items.Add(new OrderItem(@event.Sku, @event.Quantity));
        Status = "placed";
    }

    private void Apply(OrderItemAdded @event) => _items.Add(new OrderItem(@event.Sku, @event.Quantity));

    private void Apply(OrderCancelled @event) => Status = "cancelled";

    private void EnsureActive()
    {
        if (Status != "placed")
        {
            throw new InvalidOperationException("Only a placed order can be changed.");
        }
    }

    private static void EnsureValidItem(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
    }
}
