namespace EventLoom.Ordering.Api.Domain;

internal sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent<OrderPlaced, Order>
{
    public static string EventType => "ordering.order-placed";
}

internal sealed record OrderItemAdded(string Sku, int Quantity) : IDomainEvent<OrderItemAdded, Order>
{
    public static string EventType => "ordering.order-item-added";
}

internal sealed record OrderCancelled(string Reason) : IDomainEvent<OrderCancelled, Order>
{
    public static string EventType => "ordering.order-cancelled";
}

internal sealed record OrderItem(string Sku, int Quantity);

internal sealed record OrderSnapshot(string Status, IReadOnlyList<OrderItem> Items)
    : IAggregateSnapshot<OrderSnapshot, Order>
{
    public static string SnapshotType => "ordering.order";
}

internal sealed class Order(Guid id) : Aggregate<Order, Guid>(id),
    IApply<OrderPlaced>,
    IApply<OrderItemAdded>,
    IApply<OrderCancelled>,
    ISnapshotable<OrderSnapshot>
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

    OrderSnapshot ISnapshotable<OrderSnapshot>.CreateSnapshot() => new(Status, Items.ToArray());

    void ISnapshotable<OrderSnapshot>.RestoreSnapshot(OrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _items.Clear();
        _items.AddRange(snapshot.Items);
        Status = snapshot.Status;
    }

    void IApply<OrderPlaced>.Apply(OrderPlaced @event)
    {
        _items.Add(new OrderItem(@event.Sku, @event.Quantity));
        Status = "placed";
    }

    void IApply<OrderItemAdded>.Apply(OrderItemAdded @event) => _items.Add(new OrderItem(@event.Sku, @event.Quantity));

    void IApply<OrderCancelled>.Apply(OrderCancelled @event) => Status = "cancelled";

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
