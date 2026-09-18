# EventLoom.Analyzers

Roslyn diagnostics for EventLoom persisted contracts.

- `EL0001` reports concrete `IDomainEvent` types without an `EventTypeAttribute`.

Reference the package as an analyzer:

```xml
<PackageReference Include="EventLoom.Analyzers" Version="0.1.0-alpha.0"
                  PrivateAssets="all" />
```
