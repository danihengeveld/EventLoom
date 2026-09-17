---
title: Installation
description: Prepare an EventLoom development checkout.
---

EventLoom targets .NET 10. The package APIs are not available yet because the project is in its initial implementation phase.

To work on the repository, install the SDK selected by `global.json`, then run:

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```