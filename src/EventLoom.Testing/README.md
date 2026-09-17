# EventLoom.Testing

`EventLoom.Testing` contains small source-level fixture helpers for aggregate
and event-contract tests, including `EventTestBuilder<TEvent>`.

It is intentionally not part of the initial NuGet release. The project remains
in the repository while its testing surface matures beyond basic fixture
construction. Consumers should test domain behavior directly against their
aggregates and use real relational providers for persistence behavior.

```csharp
var @event = new EventTestBuilder<OrderPlaced>()
    .With(new OrderPlaced("coffee", 2))
    .Build();
```

See the [testing guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/concepts/testing.md)
for the recommended domain, relational, and PostgreSQL distributed test
boundaries.
