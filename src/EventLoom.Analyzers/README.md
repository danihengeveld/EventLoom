# EventLoom.Analyzers

Roslyn diagnostics for EventLoom persisted contracts.

- `EL0001` reports aggregate events without an `EventTypeAttribute`.
- `EL0002` reports an event whose owning aggregate has no matching `Apply` method.
- `EL0003` reports an `Apply` method that runtime dispatch cannot use.
- `EL0004` reports multiple matching handlers across an aggregate hierarchy.
- `EL0005` reports an aggregate raising an event owned by another aggregate.

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
aggregates. See [Aggregates and events](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/concepts/aggregates-and-events.md)
for the contract rules enforced by these diagnostics.
