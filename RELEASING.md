# Releasing EventLoom

EventLoom releases are immutable, tag-driven, and published to NuGet with
GitHub OpenID Connect trusted publishing. No long-lived NuGet API key is stored
in GitHub.

## One-time repository setup

### Protect `main`

Create a branch ruleset for `main` that requires pull requests, at least one
approval, resolved conversations, and these successful checks:

- `.NET (ubuntu-latest)`
- `.NET (macos-latest)`
- `.NET (windows-latest)`
- `PostgreSQL integration`
- `Packages and template`
- `Documentation`
- `Dependency review`
- `CodeQL`

Prevent branch deletion and force pushes. Require review from the owners in
`.github/CODEOWNERS` for matching files.

### Protect release tags

Create a tag ruleset targeting `v*` that prevents updates and deletion. Do not
enable **Restrict creations**: the built-in `GITHUB_TOKEN` used by the
`Prepare release` workflow cannot be selected as a ruleset bypass actor.

Tag creation remains protected by the release process: the preparation
workflow accepts only SemVer versions and tags the current `main` commit, the
release workflow verifies the tag and its `main` ancestry, and NuGet publishing
requires approval through the `nuget-release` environment. Maintainers must
not create release tags outside this workflow or move or recreate a published
tag.

### Configure the `nuget-release` environment

Create a GitHub environment named `nuget-release`:

1. Add the maintainers as required reviewers. Leave self-review enabled if a
   sole maintainer must be able to release.
2. Restrict deployments to tags matching `v*`.
3. Add the environment variable `NUGET_USER` with the NuGet.org profile
   username that owns the packages.

Do not add a NuGet API key secret.

### Configure NuGet trusted publishing

In the NuGet.org account, create a trusted-publishing policy with:

- owner: `danihengeveld`;
- repository: `EventLoom`;
- workflow file: `release.yml`;
- environment: `nuget-release`;
- package subject: `EventLoom*`;
- operation: push new packages and package versions for the first release.

After every package ID has been created, narrow the operation to pushing new
package versions only. The workflow filename is configured without the
`.github/workflows/` prefix.

### Configure Vercel

Keep Vercel connected to `main`, use `docs` as the project root, run
`pnpm build`, and deploy the generated Astro output.

## Prepare a release

1. Update current documentation and package-facing release notes.
2. Build the documentation with `pnpm --dir docs build`.
3. Commit the release changes and merge them to `main`.
4. Confirm all required checks on `main` are successful.
5. Run the `Prepare release` workflow from `main` and enter the SemVer version
   without `v`.

Supported tags are `vX.Y.Z` and SemVer prereleases such as
`vX.Y.Z-alpha.N`, `vX.Y.Z-beta.N`, and `vX.Y.Z-rc.N`.

The preparation workflow verifies the version, confirms it is running against
the current `main`, creates an annotated tag, and dispatches the tagged
release. The tag is the authoritative package version; no version-only commit
is required.

## Approve and verify publishing

The `Release` workflow:

1. verifies the tag points to a commit on `main`;
2. restores, builds, and runs all tests, including PostgreSQL;
3. packs and validates the exact package set, consumer, and template;
4. pauses at the `nuget-release` environment;
5. creates GitHub artifact attestations;
6. exchanges its GitHub OIDC token for a short-lived NuGet API key;
7. publishes packages and symbols;
8. creates a GitHub Release with generated notes and the package artifacts.

Prerelease SemVer tags create prerelease GitHub Releases. If approval is
rejected, nothing is published. If a retry is needed, rerun the existing
workflow for the same immutable tag; never create a replacement tag.

Verify the resulting package versions on NuGet.org, the GitHub Release
artifacts, their attestations with `gh attestation verify`, and the deployed
documentation.
