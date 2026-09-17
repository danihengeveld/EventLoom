# ADR 0001: Repository and Runtime Baseline

## Status

Accepted

## Decision

EventLoom is a .NET 10 monorepo. The .NET packages, tests, samples, benchmarks, and tooling reside at the repository root. The Astro/Starlight documentation site is isolated in `docs/` with its own pnpm manifest and lockfile.

The repository uses central NuGet package management, deterministic builds, nullable reference types, warnings as errors, TUnit, and GitHub Actions validation.

## Consequences

Documentation dependencies do not affect .NET consumers. Release automation must build the .NET solution and documentation site from the same commit.