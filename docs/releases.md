# Releasing Medal Sync

`Directory.Build.props` is the single version source for both programs. Stable
releases use `MAJOR.MINOR.PATCH` and the exact tag `vMAJOR.MINOR.PATCH`.
Changelog entries remain Unreleased until publication.

## Build locally

```powershell
pwsh -File scripts/validate.ps1
pwsh -File scripts/package-windows-installer.ps1
```

The first setup build downloads the compiler version pinned in
`installer/windows/toolchain.json`, verifies its SHA-256 and Authenticode
signature, then installs it per-user under ignored `.validation/build-tools/`.
The compiler is a build dependency only. End users need no PowerShell or .NET
installation to run the resulting setup EXE.

The packager performs locked, self-contained Windows x64 publishing, checks PE
subsystems, smoke-tests the CLI and verifies both executable versions. Output:

- `.medal-sync-dist/medal-sync-VERSION-setup-win-x64.exe` and its `.sha256` sidecar.
- `.medal-sync-dist/medal-sync-VERSION-win-x64.zip` and its `.sha256` sidecar.

The native setup is the primary download. It installs in Program Files, creates
shortcuts and registers the native uninstaller in Installed apps. The portable
ZIP remains available for advanced per-user deployments. It contains exactly
the two programs, portable lifecycle scripts, licenses and `release.json` with
per-file hashes. No personal configuration, database, logs or build staging
files are included. Native setup contains only the programs, a static layout
marker, offline guide, licenses and generated uninstall runtime. It also bundles
the pinned rclone executable and vendor notices, plus the pinned WinFsp MSI for
optional all-users driver installation. `scripts/setup-storage-toolchain.ps1`
downloads and verifies those dependencies into an ignored build cache without
installing them.

Setup deploys `unins000.exe` together with its `unins000.dat` installation log;
the uninstaller is embedded in setup rather than distributed as an unrelated EXE.
Installed apps and the Start menu target that executable. The Start menu also
includes a native update check and verified setup downloader, available before
configuration. Installation of a downloaded update remains explicit.

Move an existing portable archive aside before packaging again; ZIPs are not
silently overwritten. `scripts/package-windows-installer.ps1 -SkipPackage`
recompiles setup from an already verified matching ZIP.

## GitHub pipeline

Pull requests and pushes to main run [validation](../.github/workflows/validate.yml)
on standard GitHub-hosted runners, which cost no Actions minutes for this public
repository; never move these jobs to larger runners, which are always billed.
A Linux job audits source and history, checks the inventory and links and lints
the workflows. Three Windows jobs then run in parallel: .NET unit tests; the
desktop checks, script tests, both distribution formats and native install,
in-place upgrade, active-session refusal, registration, shortcuts and uninstall;
and the real rclone/WinFsp storage suite. The `validate` job passes only when
every suite passed. A pull request that changes only documentation or GitHub
issue templates skips the Windows suites; main always runs everything. NuGet
packages and the pinned build tools are cached between runs, and cached
downloads are re-checked against their SHA-256 pins. The native integration
test uses a temporary per-user program folder and refuses to run on an ordinary
desktop or over an existing installation.

`scripts/test-windows-installer-isolated.ps1` provides a separate test on an
ordinary Windows PC: it compiles a unique test identity, shortcut group, mutexes
and readiness probe while installing the unchanged production payload. It never
uses the real library or installed registration. The real rclone/WinFsp suite
in `scripts/test-storage-integration.ps1` uses disposable synthetic libraries
and named local remotes, without provider accounts.

Actions are pinned to SHAs and checkout does not retain credentials. Ordinary
validation has read-only repository permissions. Dependabot proposes monthly
action and NuGet updates, grouping minor and patch versions while keeping major
upgrades separate. See [maintainer triage](triage.md) for review and labels.

Update the version and changelog, merge validated changes to main, then tag
that commit and push the exact version tag. For version 1.0.0:

```powershell
git tag -a v1.0.0 -m 'Medal Sync 1.0.0'
git push origin v1.0.0
```

The release workflow checks tag/version equality and main ancestry, runs tests,
builds both packages, verifies native installation and creates a **draft** GitHub
release with setup EXE, portable ZIP, checksums and generated notes. It uses no
caches. Only this job has contents-write permission. Existing releases cause
failure rather than replacement.

The release job also records a signed [build provenance attestation](https://docs.github.com/en/actions/concepts/security/artifact-attestations)
for the setup EXE and portable ZIP. Anyone can check that a download was built
by this workflow from the tagged commit:

```powershell
gh attestation verify medal-sync-1.0.0-setup-win-x64.exe --repo Larson-Logan/medal-sync
```

Generated notes use [.github/release.yml](../.github/release.yml) to group PRs
by their type labels. Label merged work accurately; notes include a catch-all
for uncategorized changes and still require maintainer review.

Review assets, versions, hashes and release notes before publishing the draft.
Drafts and prereleases are invisible to automatic update checks. A published
stable setup is advertised on the next desktop launch; installation stays
explicit and verifies Ready status. Publisher code signing is not configured.

Repository visibility, branch/tag rules and vulnerability reporting remain
separate GitHub settings. No build or test deploys onto a live library.
