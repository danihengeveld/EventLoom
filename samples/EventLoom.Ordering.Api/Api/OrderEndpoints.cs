using EventLoom.EntityFrameworkCore;
using EventLoom.Hosting;
using EventLoom.Ordering.Api.Domain;
using EventLoom.Ordering.Api.Projections;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.Ordering.Api.Api;

internal static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/api/orders", CreateAsync);
        app.MapGet("/api/orders/{id:guid}", GetAsync);
        app.MapPost("/api/orders/{id:guid}/items", AddItemAsync);
        app.MapPost("/api/orders/{id:guid}/cancel", CancelAsync);
        app.MapGet("/api/orders/{id:guid}/events", GetEventsAsync);
        app.MapGet("/api/orders/{id:guid}/summary", GetSummaryAsync);
        app.MapGet("/api/projections/order-summary", GetProjectionStatusAsync);
        app.MapPost("/api/projections/order-summary/resume", ResumeProjectionAsync);
        app.MapPost("/api/projections/order-summary/replay", ReplayProjectionAsync);
        app.MapPost("/api/projections/order-summary/failures/{eventId:guid}/skip", SkipProjectionFailureAsync);
        app.MapGet("/api/outbox/{messageId:guid}", GetOutboxMessageAsync);
    }

    private static async Task<IResult> CreateAsync(
        PlaceOrderRequest request,
        AggregateRepository<Order, Guid> repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var order = new Order(request.OrderId ?? Guid.NewGuid());
        try
        {
            order.Place(request.Sku, request.Quantity);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        await repository.SaveAsync(order, RequestMetadata(context), AppendId(context), cancellationToken);
        return Results.Created($"/api/orders/{order.Id:D}", new { order.Id, order.Status, order.Items });
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        AggregateRepository<Order, Guid> repository,
        CancellationToken cancellationToken)
    {
        var order = await repository.LoadAsync(id, cancellationToken);
        return order.Version == 0
            ? Results.NotFound()
            : Results.Ok(new { order.Id, order.Status, order.Items });
    }

    private static async Task<IResult> AddItemAsync(
        Guid id,
        AddOrderItemRequest request,
        AggregateRepository<Order, Guid> repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var order = await repository.LoadAsync(id, cancellationToken);
        if (order.Version == 0)
        {
            return Results.NotFound();
        }

        try
        {
            order.AddItem(request.Sku, request.Quantity);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }

        await repository.SaveAsync(order, RequestMetadata(context), AppendId(context), cancellationToken);
        return Results.Ok(new { order.Id, order.Status, order.Items });
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelOrderRequest request,
        AggregateRepository<Order, Guid> repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var order = await repository.LoadAsync(id, cancellationToken);
        if (order.Version == 0)
        {
            return Results.NotFound();
        }

        try
        {
            order.Cancel(request.Reason);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }

        await repository.SaveAsync(order, RequestMetadata(context), AppendId(context), cancellationToken);
        return Results.Ok(new { order.Id, order.Status });
    }

    private static async Task<IResult> GetEventsAsync(
        Guid id,
        EventStore store,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken)
    {
        var history = await store.ReadStreamAsync(
            tenantAccessor.TenantId!.Value.Value,
            id.ToString("D"),
            cancellationToken: cancellationToken);
        return Results.Ok(history.Select(envelope => new
        {
            envelope.EventId,
            envelope.EventType,
            envelope.EventTypeVersion,
            envelope.StreamVersion,
            envelope.TenantOffset,
            envelope.OccurredAt,
            envelope.Metadata
        }));
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid id,
        EventStoreDbContext context,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken)
    {
        var summary = await context.Set<OrderSummary>().SingleOrDefaultAsync(
            value => value.TenantId == tenantAccessor.TenantId!.Value.Value && value.OrderId == id,
            cancellationToken);
        return summary is null ? Results.NotFound() : Results.Ok(summary);
    }

    private static async Task<IResult> GetProjectionStatusAsync(
        ProjectionAdministration administration,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken)
    {
        var key = new ProjectionKey(OrderSummaryProjection.Name, 1);
        var tenantId = tenantAccessor.TenantId!.Value.Value;
        var checkpoint = await administration.GetCheckpointAsync(tenantId, key, cancellationToken);
        var failures = await administration.ReadFailuresAsync(tenantId, key, cancellationToken: cancellationToken);
        return Results.Ok(new { checkpoint, failures });
    }

    private static async Task<IResult> GetOutboxMessageAsync(
        Guid messageId,
        OutboxAdministration administration,
        ITenantAccessor tenantAccessor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantAccessor.TenantId!.Value.Value;
        var message = await administration.GetAsync(tenantId, messageId, cancellationToken);
        if (message is null)
        {
            return Results.NotFound();
        }

        var attempts = await administration.ReadAttemptsAsync(tenantId, messageId, cancellationToken);
        return Results.Ok(new
        {
            message.MessageId,
            message.EventType,
            message.EventTypeVersion,
            message.StreamVersion,
            message.TenantOffset,
            message.AttemptCount,
            message.PublishedAt,
            attempts
        });
    }

    private static async Task<IResult> ResumeProjectionAsync(
        ProjectionAdministration administration, ITenantAccessor tenantAccessor, CancellationToken cancellationToken) =>
        (await administration.ResumeAsync(tenantAccessor.TenantId!.Value.Value, OrderSummaryKey, cancellationToken))
            ? Results.NoContent()
            : Results.Conflict(new { error = "The projection is not paused." });

    private static async Task<IResult> ReplayProjectionAsync(
        ProjectionAdministration administration, ITenantAccessor tenantAccessor, CancellationToken cancellationToken)
    {
        await administration.ReplayAsync(tenantAccessor.TenantId!.Value.Value, OrderSummaryKey, cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> SkipProjectionFailureAsync(
        Guid eventId, ProjectionAdministration administration, ITenantAccessor tenantAccessor, CancellationToken cancellationToken) =>
        (await administration.SkipAsync(tenantAccessor.TenantId!.Value.Value, OrderSummaryKey, eventId, cancellationToken))
            ? Results.NoContent()
            : Results.NotFound();

    private static ProjectionKey OrderSummaryKey => new(OrderSummaryProjection.Name, 1);

    private static EventMetadata RequestMetadata(HttpContext context) =>
        new(
            CorrelationId: context.Request.Headers["X-Correlation-ID"].FirstOrDefault(),
            Actor: "ordering-api");

    private static string? AppendId(HttpContext context) =>
        context.Request.Headers["Idempotency-Key"].FirstOrDefault();
}

internal sealed record PlaceOrderRequest(string Sku, int Quantity, Guid? OrderId = null);

internal sealed record AddOrderItemRequest(string Sku, int Quantity);

internal sealed record CancelOrderRequest(string Reason);
