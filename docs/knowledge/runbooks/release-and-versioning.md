---
type: runbook
title: Release and versioning
description: Steps to land a change on dev, publish betas, prepare a release and promote dev to main, plus the version numbering rules the checks enforce.
resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/releases.md
tags: [release, versioning, changelog, beta, promotion, ci]
sources:
  - id: releases
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/releases.md
    title: Releasing Medal Sync
  - id: versioning
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/versioning.md
    title: Versions and schemas
  - id: workflows
    resource: https://github.com/Larson-Logan/medal-sync/tree/dev/.github/workflows
    title: GitHub Actions workflows
generated: { by: "claude-code/claude-opus-5-5", at: "2026-11-20T03:34:40Z" }
status: draft
---

# Summary

`dev` is the default branch and receives every pull request. Every push to
`dev` publishes a beta. `main` only receives `dev` through a promotion pull
request, and every merge to `main` publishes a release. The full process is in
[releasing Medal Sync](../../releases.md).

# Version numbers

- Releases are `MAJOR.MINOR.PATCH.NN`, tagged `vMAJOR.MINOR.PATCH.NN`.
  `MAJOR.MINOR.PATCH` is `VersionPrefix` in
  [`Directory.Build.props`](../../../Directory.Build.props). `NN` is a hotfix
  counter set by the release workflow: `.00` when `VersionPrefix` changes,
  otherwise one more than the last release.
- Betas are `vMAJOR.MINOR.PATCH.NN-beta.N`.
- A breaking (`type!:`) pull request requires a MAJOR raise, and a `feat`
  requires a MINOR raise. See [versions and schemas](../../versioning.md).

# Land a change on dev

1. Branch from current `dev` as `PREFIX/short-topic`. Agent sessions use their
   own prefix, such as `claude/`.
2. Record user-visible changes under `## Unreleased` in `CHANGELOG.md`.
3. After adding, removing or renaming files, run
   `python scripts/update-source-manifest.py` (see
   [public source hygiene](../policies/public-source-hygiene.md)).
4. Validate with `pwsh -File scripts/validate.ps1`, or the focused documentation
   checks in [CONTRIBUTING](../../../CONTRIBUTING.md#validate-your-change).
5. Open a pull request into `dev` with a title in the form `type: summary` or
   `type(scope): summary`. Pull requests are squash-merged, so the title becomes
   the commit and its release-note line.

# Prepare and promote a release

1. `git fetch origin main dev --tags`, then
   `git switch -c release/prepare origin/dev`.
2. `python scripts/prepare-release.py --dry-run`, then run it without
   `--dry-run`. It chooses the version, raises `VersionPrefix` if needed and
   renames `## Unreleased` to the release heading.
3. Commit the two files and merge that pull request into `dev`.
4. Preview the promotion check with
   `python scripts/check-promotion.py --main origin/main --dev origin/dev`.
5. Open a pull request from `dev` to `main` and merge it (merge commit) once
   `validate` is green. A promotion reuses the beta validation of the identical
   `dev` commit when one exists.

# Build locally

`pwsh -File scripts/validate.ps1` and
`pwsh -File scripts/package-windows-installer.ps1`. Local builds are numbered
`VERSIONPREFIX.00`; set `MedalReleaseRevision` to build another hotfix number.

# Related

- [Public source hygiene](../policies/public-source-hygiene.md): checks that
  every pull request must pass.
- [Library ownership](../architecture/library-ownership.md): file `format`
  changes need a major release.
- [Promote dev to main](../../releases.md#promote-dev-to-main) and
  [beta releases](../../releases.md#beta-releases)
