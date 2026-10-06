# EventLoom.Analyzers

The analyzer project still exists in the repository, but it is bundled into
the `EventLoom` package under `analyzers/dotnet/cs` and is not published as a
standalone NuGet package. Projects that reference `EventLoom` receive these
EventLoom diagnostics automatically.

The C# compiler now enforces the core contracts through static-abstract
interfaces: events implement `IDomainEvent<TSelf, TAggregate>`, aggregates use
`Aggregate<TSelf, TId>` and `IApply<TEvent>`, and snapshots implement
`IAggregateSnapshot<TSelf, TAggregate>` with `ISnapshotable<TSnapshot>` on the
aggregate. The analyzer adds deterministic, immutability, and safety checks on
top of those compiler constraints.

## Diagnostics

| ID | Severity | Reported when |
| --- | --- | --- |
| `EL0101` | Error | An aggregate does not pass itself as `TSelf`; abstract generic bases may pass their type parameter. |
| `EL0102` | Error | `Raise` is called inside `Apply`, `CreateSnapshot`, or `RestoreSnapshot`. |
| `EL0103` | Error | `Raise` is called from an aggregate constructor. |
| `EL0104` | Error | Application code directly calls `IApply<T>` or `ISnapshotable<T>` members that EventLoom owns. |
| `EL0105` | Error | Event handlers or snapshot callbacks use nondeterminism such as clocks, GUID/random generation, environment, files, HTTP, tasks, or async handlers. |
| `EL0106` | Error | `EventType`/`SnapshotType` is not a non-empty constant string, or `EventVersion`/`SnapshotVersion` is not a positive constant integer. |
| `EL0107` | Error | Duplicate event or snapshot names are declared within one compilation. |
| `EL0108` | Error | Event or snapshot DTOs are mutable, including non-init setters or non-readonly fields. |
| `EL0109` | Info | An aggregate implements `IApply<T>` for an event owned by another aggregate; the handler is dead registration-wise. |
| `EL0110` | Info, disabled by default | Aggregate state is assigned outside handlers or constructors. |

## Retired diagnostic mapping

The `EL0001`-`EL0010` IDs are retired and are not reused.

| Retired ID | Previous check | Replacement |
| --- | --- | --- |
| `EL0001` | Missing event attribute metadata. | Compiler requires `EventType`; `EL0106` validates non-empty constant strings and positive versions. |
| `EL0002` | Missing aggregate `Apply(TEvent)` handler. | `IDomainEvent<TSelf, TAggregate>` fails to compile unless the aggregate implements `IApply<TSelf>` (for example CS0311). |
| `EL0003` | Invalid `Apply` method shape or accessibility. | `IApply<TEvent>.Apply` is an interface contract; `EL0104` prevents direct application calls. |
| `EL0004` | Ambiguous `Apply` handlers across a hierarchy. | The interface contract removes name-based handler discovery; use one `IApply<TEvent>` implementation on the concrete aggregate. |
| `EL0005` | Raising an event owned by another aggregate. | `Raise<TEvent>` is constrained to events owned by `TSelf`; invalid calls fail to compile. |
| `EL0006` | Missing snapshot attribute metadata. | Compiler requires `SnapshotType`; `EL0106` validates non-empty constant strings and positive versions. |
| `EL0007` | Missing `CreateSnapshot` method. | `IAggregateSnapshot<TSelf, TAggregate>` requires the aggregate to implement `ISnapshotable<TSelf>`. |
| `EL0008` | Invalid `CreateSnapshot` shape. | `ISnapshotable<TSnapshot>.CreateSnapshot` is an interface contract; `EL0104` prevents direct calls. |
| `EL0009` | Missing `RestoreSnapshot` method. | `IAggregateSnapshot<TSelf, TAggregate>` requires the aggregate to implement `ISnapshotable<TSelf>`. |
| `EL0010` | Invalid `RestoreSnapshot` shape. | `ISnapshotable<TSnapshot>.RestoreSnapshot` is an interface contract; `EL0104` prevents direct calls. |

A committed event-contract baseline file to catch renamed `EventType` strings
and version decreases is planned but not yet available.
