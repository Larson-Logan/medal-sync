# Releasing Medal Sync

`Directory.Build.props` holds `VersionPrefix` (`MAJOR.MINOR.PATCH`) for both
programs. Every merge to `main` publishes a release tagged
`vMAJOR.MINOR.PATCH.NN`, where `NN` is a hotfix counter: `.00` when the merge
changes `VersionPrefix`, otherwise one more than the last release of that
version.
[Versions and schemas](versioning.md) explains which part to raise and how file
formats and Medal schemas are versioned.

Record user-visible changes under `## Unreleased` in `CHANGELOG.md` as they
merge to `dev`. Before promoting, rename that heading on `dev` to the release
the promotion will publish, such as `## 1.4.0 — DATE` for a new version or
`## 1.3.0.01 — DATE` for a hotfix, and start a new empty `## Unreleased`
above it. The release workflow does not edit the changelog.

## Build locally

```powershell
pwsh -File scripts/validate.ps1
pwsh -File scripts/package-windows-installer.ps1
```

Local builds are numbered `VERSIONPREFIX.00`. Set the `MedalReleaseRevision`
environment variable, for example to `03`, to build another hotfix number.

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

## Branches

| Branch | Purpose | Rules |
| --- | --- | --- |
| `dev` | Default branch. Every pull request targets it; it may be briefly broken. | Pull request required; `Repository checks` (privacy audit, inventory, links, workflow lint) must pass; merge or squash. |
| `main` | Always works. Releases are built only from it. | Only `dev` may merge in, through a pull request whose `validate` check ran every suite; merge commits only, so `dev` history and its pull requests carry over. |

To promote, open a pull request from `dev` to `main`, label it `promotion` so
release notes skip it, and merge it once `validate` is green. Fix anything it
finds on `dev` first. The branch and tag rules are kept in
[.github/rulesets](../.github/rulesets); see
[security maintenance](security-maintenance.md#branches-tags-and-actions).

Merged topic branches are deleted automatically by the repository setting,
which `scripts/setup-github-rulesets.ps1` maintains. The
[branch cleanup workflow](../.github/workflows/branch-cleanup.yml) also handles
closed, unmerged pull requests. It preserves `main`, `dev`, the default branch,
protected branches, forks, branches used as the head or base of an open PR, and
branches with commits added since closure. It runs only trusted default-branch
code. A maintainer can rerun it manually with a closed PR number, or preview
cleanup with `python scripts/cleanup-pr-branch.py --repository OWNER/REPO --pr NUMBER`;
add `--apply` to delete the branch. Local clones should set
`git config fetch.prune true` to discard deleted remote-tracking references on
fetch; local topic branches and worktrees require separate cleanup after
checking for unmerged commits and local edits.

## GitHub pipeline

Pull requests and pushes to `dev` and `main` run [validation](../.github/workflows/validate.yml)
on standard GitHub-hosted runners, which cost no Actions minutes for this public
repository; never move these jobs to larger runners, which are always billed.
A Linux job audits source and history, checks the inventory and links and lints
the workflows. Three Windows jobs then run in parallel: .NET unit tests; the
desktop checks, script tests, both distribution formats and native install,
in-place upgrade, active-session refusal, registration, shortcuts and uninstall;
and the real rclone/WinFsp storage suite. The `validate` job passes only when
every suite passed or was skipped as unaffected.

[public-source.json](../public-source.json) lists every source file with the
Windows suites it affects, generated by rules in
[update-source-manifest.py](../scripts/update-source-manifest.py). A pull
request into `dev` runs only the suites its changed files need, so a
documentation change runs none and a unit-test change runs only the unit tests;
the run summary names the files behind each choice. A file no rule names needs
every suite. Pushes and pull requests into `main` always run everything. NuGet
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

Plan each release with its milestone, titled with the version; see
[milestones](triage.md#milestones). Merged pull requests and the issues they
close join it automatically, so before raising the version check the project's
Releases view for anything still open in that milestone and finish it or move
it to the next one.

Every promotion to `main` is a release. To start a new version, raise
`VersionPrefix` and update the changelog in a pull request to `dev`, merge it,
then promote `dev` to `main`; that release is `.00`. To ship a hotfix, merge
the fix to `dev` and promote it without touching `VersionPrefix`; it is
released as the next number, such as `.01`. Changing `VersionPrefix` to a
version that already has a release is refused.

The first release after 1.2.0 (`LAST_LEGACY_RELEASE` in the workflow) was
the three-part bridge `v1.3.0`, because update checks in 1.2.0 and earlier
ignore four-part tags. It counts as `.00`; installs that update to it can then
find every later release.

Older installs only check the release marked Latest, and the first hotfix,
`v1.3.0.01`, replaces the bridge as Latest. From then on, a 1.2.0 or
earlier install that never took the bridge sees no update: the desktop check
stays silent and the portable updater reports that it expected a stable
release. Its user must download the newest setup from the
[Releases page](https://github.com/Larson-Logan/medal-sync/releases) once;
later updates are found normally. To give older installs time to take the
bridge, leave a few days before promoting the first hotfix unless the fix is
urgent.

The [release workflow](../.github/workflows/release.yml) starts on its own for
every push to `main`, or from **Run workflow** on the Actions tab for `main`;
it never builds another branch. Runs never overlap, so two merges never take
the same number. It does nothing when the commit is no longer the tip of
`main`, so rerunning an older failed run never publishes older code under a
newer number, or when a release, draft or published, already targets that
commit, so a rerun never publishes twice.

Otherwise it checks the version format and main ancestry, refuses a `v*` tag
that already points at a different commit, runs tests, builds both packages
with the chosen number, verifies native installation, uploads setup EXE,
portable ZIP, checksums and generated notes to a draft, then publishes it. The
run summary links the release. It uses no caches. Only the planning job, which
must see drafts, and the app token have contents-write permission. Existing releases cause failure rather than
replacement.

Publishing creates the version tag on the exact commit that was built. The
release tag ruleset lets only administrators and the private
`medal-sync-release` GitHub App create, move or delete a `v*` tag; rulesets
cannot exempt `github-actions[bot]`. So the publish step alone uses a
short-lived token for that app, minted by
[create-github-app-token](https://github.com/actions/create-github-app-token)
with contents-write only; every other step uses the workflow's own read-only
token. The app is installed on this repository only. Its client ID is the
repository variable `RELEASE_APP_CLIENT_ID` and its private key the repository
secret `RELEASE_APP_PRIVATE_KEY`; see
[security maintenance](security-maintenance.md#release-app) to rotate it. Do
not create a release or push a tag by hand. If publishing fails after the
upload, the draft stays: review it and publish it by hand, or delete it and
choose **Run workflow**.

The release job also records a signed [build provenance attestation](https://docs.github.com/en/actions/concepts/security/artifact-attestations)
for the setup EXE and portable ZIP. Anyone can check that a download was built
by this workflow from the released commit:

```powershell
gh attestation verify medal-sync-1.3.0.01-setup-win-x64.exe --repo Larson-Logan/medal-sync
```

Generated notes use [.github/release.yml](../.github/release.yml) to group PRs
by their type labels. Label merged work accurately; notes include a catch-all
for uncategorized changes and still require maintainer review.

For a `.00` or bridge release, the run summary also lists any item still open in the
version's milestone. Because the app publishes the release, the
[milestones workflow](../.github/workflows/milestones.yml) runs on it and
closes that milestone once it is empty. Milestones are titled
`MAJOR.MINOR.PATCH`; hotfix releases do not have their own.

Releases are published without a manual review, so `validate` on the
promotion pull request is the last gate. Drafts and prereleases are invisible
to automatic update checks. A published stable setup is advertised on the
next desktop launch; installation stays
explicit and verifies Ready status. Publisher code signing is not configured.

Repository visibility, branch/tag rules and vulnerability reporting remain
separate GitHub settings. No build or test deploys onto a live library.
