---
title: Serialization and event evolution
description: Register stable event schemas, opt into source-generated JSON, and evolve historical payloads.
---

EventLoom serializes registered events with `System.Text.Json`. A stored event
is located by its stable event name and schema version, never by its CLR type
name.

## Default serialization

Reflection serialization is enabled by default:

```csharp
services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .AddEvent<OrderPlaced>();
```

This is the simplest option for ordinary server applications.

## Source-generated metadata

For trimming or Native AOT-oriented applications, generate metadata in your
application and register its context:

```csharp
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderCancelled))]
public partial class OrderingJsonContext : JsonSerializerContext;

services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .AddJsonSerializerContext(OrderingJsonContext.Default)
    .AddEvent<OrderPlaced>()
    .AddEvent<OrderCancelled>();
```

Register every event type that can be persisted or deserialized. EventLoom
currently retains reflection fallback; strict source-generated-only execution
is available from `EventSerializer` construction for advanced composition.

## Evolve an event without retaining old CLR types

Increase `EventType.Version` when a serialized payload changes. Keep one
current CLR event type and register deterministic upcasters for each historical
step:

```csharp
using System.Text.Json;

[EventType("orders.order-placed", Version = 2)]
public sealed record OrderPlaced(string Sku, int Quantity, string Currency)
    : IDomainEvent<Order>;

public sealed class OrderPlacedV1ToV2 : IEventUpcaster
{
    public string EventName => "orders.order-placed";
    public int FromVersion => 1;
    public int ToVersion => 2;

    public JsonElement Upcast(JsonElement payload)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            payload.GetRawText())
            ?? throw new InvalidOperationException("The v1 payload was empty.");
        fields["currency"] = JsonSerializer.SerializeToElement("EUR");
        return JsonSerializer.SerializeToElement(fields);
    }
}
```

Register the current event and each upcaster:

```csharp
eventLoom
    .AddEvent<OrderPlaced>()
    .AddUpcaster(new OrderPlacedV1ToV2());
```

Upcasters must advance exactly one version, be deterministic, and have no
database, clock, network, or other external side effects. EventLoom rejects
ambiguous chains and throws if a stored payload cannot reach the current
version.

Do not modify the meaning of an existing serialized field in place. Add a new
event when the business fact itself changes; use an upcaster only to preserve
the meaning of an existing fact.
