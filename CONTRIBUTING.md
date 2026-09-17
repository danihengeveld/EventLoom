# Contributing to EventLoom

Use the .NET SDK selected by `global.json` and pnpm for the documentation site. Before opening a pull request, run:

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```

Changes to persistence guarantees, public APIs, or operational behavior require an architecture decision record or an update to an existing decision.