# EventLoom.Analyzers

Roslyn diagnostics for EventLoom persisted contracts.

- `EL0001` reports aggregate events without an `EventTypeAttribute`.
- `EL0002` reports an event whose owning aggregate has no matching `Apply` method.
- `EL0003` reports an `Apply` method that runtime dispatch cannot use.
- `EL0004` reports multiple matching handlers across an aggregate hierarchy.
- `EL0005` reports an aggregate raising an event owned by another aggregate.

Reference the package as an analyzer:

```xml
<PackageReference Include="EventLoom.Analyzers" Version="0.1.0-alpha.0"
                  PrivateAssets="all" />
```
