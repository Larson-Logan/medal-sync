# Releasing Medal Sync

`Directory.Build.props` is the single version source for both programs. Stable
releases use `MAJOR.MINOR.PATCH` and the exact tag `vMAJOR.MINOR.PATCH`.
Prerelease channels are not implemented. Changelog entries remain Unreleased
until a maintainer actually publishes the release.

## Build locally

```powershell
pwsh -File scripts/validate.ps1
pwsh -File scripts/package-release.ps1
```

The packager performs locked, self-contained Windows x64 publishing, checks PE
subsystems, smoke-tests the CLI, verifies both executable versions and creates:

- `.medal-sync-dist/medal-sync-VERSION-win-x64.zip`
- The matching `.zip.sha256` file.

The ZIP contains exactly the CLI, windowless launcher, updater, shared module,
startup helper, LICENSE, THIRD_PARTY.md, the SDK distribution's .NET license
and third-party notices, and `release.json`. Every payload file
has a SHA-256 entry in the manifest. Packaging opens and verifies its own ZIP.
No config, databases, logs or build staging directories are included.
Move an existing archive aside before packaging again; releases are never
silently overwritten. `-SkipBuild` packages already-built matching binaries.

## GitHub pipeline

Pull requests and pushes to main run Windows validation and package smoke
checks. Actions are pinned to commit SHAs, credentials are not retained by
checkout and ordinary validation has read-only permissions. Dependabot proposes
monthly action and NuGet updates.

For a release, update the version/changelog, merge validated changes to main,
then tag that commit and push the tag. For example, for version 0.2.0:

```powershell
git tag -a v0.2.0 -m 'Medal Sync 0.2.0'
git push origin v0.2.0
```

The release workflow requires exact tag/version equality and main ancestry,
runs the full suite, packages the programs and creates a **draft** GitHub release
with ZIP, checksum and generated notes. Only this job has contents-write access.
Existing releases cause failure rather than replacement. A failed run can be
retried while no release exists; inspect partial drafts before retrying.

Review the draft's assets, version, checksums and notes, and complete applicable
desktop acceptance before publishing it. Drafts and prereleases are invisible
to automatic update checks. Publishing a stable release exposes its GitHub
asset digest to the updater. New versions are advertised on the next desktop
launch; installation remains explicit and requires Ready status.

Branch rules, tag rules, secret scanning, private vulnerability reporting and
repository visibility are GitHub settings managed separately from these files.
