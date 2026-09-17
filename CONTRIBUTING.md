# Contributing to EventLoom

Use the .NET SDK selected by `global.json` and pnpm for the documentation site. Before opening a pull request, run:

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```

Changes to persistence guarantees, public APIs, or operational behavior require an architecture decision record or an update to an existing decision.

## Public API and package validation

The six supported library packages maintain nullable-aware public API baselines
in their `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` files. Until the
first published package version, public APIs belong in the unshipped baseline.
Move intentional, published contract signatures to the shipped baseline as part
of a release; do not remove or alter shipped signatures without an explicitly
documented compatibility decision.

CI packs only the supported library projects and restores
`tests/EventLoom.PackageConsumerTests` from that local package feed. Keep the
consumer scenario independent of project references so it continues to verify
the actual NuGet dependency graph.