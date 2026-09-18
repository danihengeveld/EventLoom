# EventLoom documentation

This directory contains the EventLoom documentation site, built with
[Astro Starlight](https://starlight.astro.build/).

## Information architecture

The site uses four intentional content types:

- **Start here** is an ordered onboarding path for developers new to
  EventLoom.
- **Core concepts** explain durable ideas and trade-offs. They answer *what* and
  *why*, rather than presenting a recipe.
- **Guides** help a developer accomplish one concrete task. They should
  include prerequisites, a smallest complete example, safety constraints, and
  a verification or recovery path where relevant.
- **Reference** documents exact, stable facts such as configuration defaults
  and support boundaries.

Keep guides in the order a developer is likely to adopt capabilities:
configuration, append/read, snapshots, projections, outbox, testing,
observability, and deployment. Do not classify a feature by an arbitrary
"build" versus "operate" boundary: snapshots, projections, and outbox all
have design-time and runtime concerns, so each belongs in the same guide
sequence with its operations and recovery behavior.

## Local development

Run these commands from this directory:

```bash
pnpm install
pnpm dev
pnpm build
pnpm preview
```

`pnpm dev` starts the site at `http://localhost:4321`. `pnpm build` writes the
production output to `dist/` and is the required validation for documentation
changes.

Source pages live in `src/content/docs/`; their paths become site routes.
Navigation is maintained explicitly in `astro.config.mjs`.
