# EventLoom

`EventLoom` provides the core domain abstractions for immutable, versioned
domain events and event-sourced aggregates.

Use this package to define `IDomainEvent` contracts, stable `[EventType]`
names, aggregates, event metadata, expected-version rules, and serialization
registrations. Add `EventLoom.EntityFrameworkCore` and a provider package to
persist events.

EventLoom targets .NET 10. It is pre-release software and is not published to
NuGet yet.

See the [getting started guide](https://github.com/danihengeveld/EventLoom/tree/main/docs/src/content/docs/getting-started)
and [architecture documentation](https://github.com/danihengeveld/EventLoom/tree/main/docs/architecture).
