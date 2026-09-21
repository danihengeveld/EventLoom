# EventLoom.Analyzers

Roslyn diagnostics for EventLoom persisted event and aggregate snapshot
contracts. All diagnostics are enabled by default and reported as errors
because violating these contracts prevents reliable runtime dispatch or
persistence.

## Domain event diagnostics

`DomainEventContractAnalyzer` validates aggregate-owned domain events:

| ID | Reported when |
| --- | --- |
| `EL0001` | A concrete `IDomainEvent<TAggregate>` does not declare `EventTypeAttribute`. |
| `EL0002` | The owning aggregate has no matching `Apply(TEvent)` handler. |
| `EL0003` | An `Apply` method cannot be used by runtime dispatch because its signature or accessibility is invalid. |
| `EL0004` | Multiple matching `Apply(TEvent)` handlers exist across an aggregate hierarchy. |
| `EL0005` | An aggregate calls `Raise` with an event owned by an unrelated aggregate. |

## Aggregate snapshot diagnostics

`AggregateSnapshotContractAnalyzer` validates snapshot identity and the private
methods used by runtime snapshot dispatch:

| ID | Reported when |
| --- | --- |
| `EL0006` | A concrete `IAggregateSnapshot<TAggregate>` does not declare `SnapshotTypeAttribute`. |
| `EL0007` | The owning aggregate has no private `TSnapshot CreateSnapshot()` method. |
| `EL0008` | A `CreateSnapshot` method has an invalid return type, parameters, accessibility, or modifiers. |
| `EL0009` | The owning aggregate has no private `void RestoreSnapshot(TSnapshot)` method. |
| `EL0010` | A `RestoreSnapshot` method has invalid parameters, return type, accessibility, or modifiers. |

The event and snapshot analyzers are independent. Projects that expose only
one contract family still receive all relevant diagnostics.

## Installation

Reference the package as an analyzer:

> **Planned package reference:** EventLoom packages are not published to NuGet
> yet. Reference the analyzer project from a checkout while evaluating the
> library:

```xml
<ProjectReference Include="../EventLoom/src/EventLoom.Analyzers/EventLoom.Analyzers.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

```xml
<PackageReference Include="EventLoom.Analyzers" Version="0.1.0-alpha.0"
                  PrivateAssets="all" />
```

The analyzer package is part of the pre-release distribution surface and is
recommended for every project that defines persisted EventLoom events and
aggregates. See
[Aggregates and events](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/concepts/aggregates-and-events.md)
and
[Snapshots](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/snapshots.md)
for the contract rules enforced by these diagnostics.
