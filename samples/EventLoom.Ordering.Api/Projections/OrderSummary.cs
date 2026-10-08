namespace EventLoom.Ordering.Api.Projections;

internal sealed class OrderSummary
{
    public required string TenantId { get; set; }
    public Guid OrderId { get; set; }
    public required string Status { get; set; }
    public int ItemCount { get; set; }
    public int TotalQuantity { get; set; }
    public long TenantOffset { get; set; }
}
