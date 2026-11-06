# Releasing Medal Sync

`Directory.Build.props` holds `VersionPrefix` (`MAJOR.MINOR.PATCH`) for both
programs. Every merge to `main` publishes a release tagged
`vMAJOR.MINOR.PATCH.NN`, where `NN` is a hotfix counter: `.00` when the merge
changes `VersionPrefix`, otherwise one more than the last release of that
version. Every merge to `dev` also publishes a beta of the next release, such
as `v1.3.1.02-beta.1`; see [beta releases](#beta-releases).
[Versions and schemas](versioning.md) explains which part to raise and how file
formats and Medal schemas are versioned.

Record user-visible changes under `## Unreleased` in `CHANGELOG.md` as they
merge to `dev`. Before promoting, rename that heading on `dev` to the release
the promotion will publish, such as `## 1.4.0 — DATE` for a new version or
`## 1.3.0.01 — DATE` for a hotfix, and start a new empty `## Unreleased`
above it. The release workflow does not edit the changelog; the
[promotion check](#promote-dev-to-main) refuses a promotion that skips this.

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
| `dev` | Default branch. Every pull request targets it. | Pull request required; `Repository checks`, `validate` (for the suites the change affects) and `PR title` must pass; review threads resolved; squash only. |
| `main` | Always works. Releases are built only from it. | Only `dev` may merge in, through a pull request whose `validate` check ran every suite or reused the passing beta validation of the identical `dev` commit; merge commits only, so `dev` history and its pull requests carry over. |

Topic branches start from current `dev`, hold one focused change and are
named `PREFIX/short-topic`, such as `fix/107-rclone-password-prompt`; agent
sessions use their own prefix, such as `claude/` or `codex/`. Because pull
requests into `dev` are squashed, the pull request title becomes the commit on
`dev` and its line in release notes. Titles read `type: summary` or
`type(scope): summary`, using one of `feat`, `fix`, `perf`, `docs`, `ci`,
`build`, `deps`, `refactor`, `test`, `chore`, `release` or `revert`; add `!`
after the type for a breaking change. The
[title check](../.github/workflows/pull-request-title.yml) fails other titles
and sets the release-note label from the type: `feat` and `perf` become
`enhancement`, `fix` becomes `bug`, `docs` becomes `documentation` and `deps`
becomes `dependencies`. The title is the source of truth: changing its type
replaces that label. Forks and Dependabot keep the labels they have.

After a pull request merges, a branch built on top of it needs
`git rebase --onto origin/dev OLD_BASE`, where `OLD_BASE` is the last commit
of the merged branch, because the squash commit replaces the original commits. Do not merge `dev` into a topic branch to combine changes for
testing; branches need not be up to date to merge, and validation tests the
merge result.

The branch and tag rules are kept in
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

### Promote dev to main

First prepare the release on a branch from `dev`. The
[release preparation script](../scripts/prepare-release.py) chooses the version
from the pull request titles that `dev` has and `main` does not, raises
`VersionPrefix` when they need it and renames `## Unreleased` to the release
heading:

```powershell
git fetch origin main dev --tags
git switch -c release/prepare origin/dev
python scripts/prepare-release.py --dry-run   # show the plan only
python scripts/prepare-release.py
```

It prints the version, the pull request title, such as
`release: prepare 1.5.0`, and the promoted pull requests grouped for the
promotion's description. Commit the two files and merge that pull request into
`dev`. If more pull requests merge before the promotion, run it again: it moves
their entries, and any section prepared for a release that never shipped, such
as a hotfix that a later feature turned into a new minor version, under one
heading. On a branch that is already prepared it changes nothing.

Then open a pull request from `dev` to `main` and merge it once
`validate` is green. Its title is free-form, and the title check labels it
`promotion` so release notes skip it. Fix anything it finds on `dev` first.

On every pull request into `main`, validation runs the
[promotion check](../scripts/check-promotion.py) before any suite. It compares
the `dev` commit with `main` and fails when:

- a squashed pull request in the promotion has a breaking title (`type!:`)
  and `VersionPrefix` does not raise MAJOR, or a `feat` title and it does not
  raise MINOR;
- `VersionPrefix` is lower than on `main`, or already released;
- `CHANGELOG.md` still has entries under `## Unreleased`, or its newest
  heading does not name the release this promotion publishes, such as
  `## 1.4.0 — DATE` for a new version or `## 1.4.0.01 — DATE` for a hotfix,
  or the section below that heading was prepared for a release that never
  shipped. A release with only internal changes still needs a heading that
  says so.

The check chooses the version as the release workflow does and names it in
the run summary. Preview it before opening the promotion:

```powershell
git fetch origin main dev --tags
python scripts/check-promotion.py --main origin/main --dev origin/dev
```

## GitHub pipeline

Pull requests run [validation](../.github/workflows/validate.yml), and a newer
push to a pull request cancels its older run. Pushes to `dev` and `main` run it
only through the [beta](#beta-releases) and release workflows, so each commit is
validated once and publishing waits for every suite. All jobs use
standard GitHub-hosted runners, which cost no Actions minutes for this public
repository; never move these jobs to larger runners, which are always billed.
A Linux job audits source and history, checks the inventory and links and lints
the workflows. Three Windows jobs then run in parallel: .NET unit tests; the
desktop checks, script tests, both distribution formats and native install,
in-place upgrade, active-session refusal, registration, shortcuts and uninstall;
and the real rclone/WinFsp storage suite. The `validate` job passes only when
every suite passed or was skipped as unaffected or already passed in `dev`'s
beta run.

[public-source.json](../public-source.json) lists every source file with the
Windows suites it affects, generated by rules in
[update-source-manifest.py](../scripts/update-source-manifest.py). A pull
request into `dev` runs only the suites its changed files need, so a
documentation change runs none and a unit-test change runs only the unit tests;
the run summary names the files behind each choice. A file no rule names needs
every suite. Beta and release runs always run everything. A promotion merges
to exactly the tree of the `dev` commit it promotes, so its pull request reuses
the validation from that commit's beta run, waiting up to 25 minutes while it
is still going. If the trees differ, the commit has no beta run, or its
validation did not pass, the promotion runs every suite itself. NuGet
packages and the pinned build tools are cached in ordinary validation, and
cached downloads are re-checked against their SHA-256 pins. Release validation
uses no caches. The native integration
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
released as the next number, such as `.01`. Either way,
[prepare the release](#promote-dev-to-main) first. Changing `VersionPrefix` to a
version that already has a release is refused. A hotfix carries fixes only:
once `dev` holds a `feat` or breaking pull request, the
[promotion check](#promote-dev-to-main) requires a new MINOR or MAJOR version.

The first release after 1.2.0 was the one-time three-part bridge `v1.3.0`,
because update checks in 1.2.0 and earlier ignore four-part tags. It counts as
`1.3.0.00`. Those older installs only check the release marked Latest, and
`v1.3.0.01` replaced the bridge as Latest, so a 1.2.0 or earlier install that
never took the bridge sees no update: the desktop check stays silent and the
portable updater reports that it expected a stable release. Its user must
download the newest setup from the
[Releases page](https://github.com/Larson-Logan/medal-sync/releases) once;
later updates are found normally.

The [release workflow](../.github/workflows/release.yml) starts on its own for
every push to `main`, or from **Run workflow** on the Actions tab for `main`;
it never builds another branch. Runs never overlap, so two merges never take
the same number. It does nothing when the commit is no longer the tip of
`main`, so rerunning an older failed run never publishes older code under a
newer number, or when a release, draft or published, already targets that
commit, so a rerun never publishes twice.

Otherwise it checks the version format and main ancestry, refuses a `v*` tag
that already points at a different commit and calls the full validation
workflow with the chosen number and caches disabled. That workflow builds both
packages once, tests native installation and retains the tested setup EXE,
portable ZIP and checksums as the `release-assets` artifact for seven days.
Installer failure logs are retained for the same period.

Only after every suite passes does a separate job download those exact files,
verify their names and SHA-256 sidecars, attest them, upload them with generated
notes to a draft and publish it. The run summary links the release. Only the
planning job, which must see drafts, and the app token have contents-write
permission. Existing releases cause failure rather than replacement. A retry
of failed jobs reuses a successful build's artifact; if it has expired, rerun
all jobs to rebuild and validate it.

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

For a `.00` release, the run summary also lists any item still open in the
version's milestone. Because the app publishes the release, the
[milestones workflow](../.github/workflows/milestones.yml) runs on it and
closes that milestone once it is empty. Milestones are titled
`MAJOR.MINOR.PATCH`; hotfix releases do not have their own.

Releases are published without a manual review. `validate` on the promotion
pull request gates `main`, and the release run's own uncached validation of
every suite gates publishing. Drafts are invisible to automatic update
checks, and prereleases are too unless a PC opts in to
[betas](#beta-releases). A published stable setup is advertised on the
next desktop launch; installation stays
explicit and verifies Ready status.

### Code signing

Stable releases are signed for free through SignPath Foundation once the
project is accepted; the [code signing policy](code-signing.md) lists what is
signed and who approves it. Signing is off until the repository variable
`SIGNPATH_ORGANIZATION_ID` and the secret `SIGNPATH_API_TOKEN` are set; until
then releases are built unsigned exactly as before. With both set, the release
run's validation signs `medal-sync.exe` and `medal-sync-launch.exe` before
packaging, then signs the setup EXE it builds from them, waiting up to 40
minutes for each approval. Approve both requests in SignPath when a release
runs. The installer tests then run against the signed setup, and the publish
job refuses any file without a valid, timestamped signature. Betas are never
signed.

To turn it on after acceptance:

1. In SignPath, create the project (slug `medal-sync`), link it to this
   repository as a GitHub trusted build system, and add the `programs` and
   `setup` artifact configurations from the
   [code signing policy](code-signing.md#how-a-release-is-signed).
2. Use the `release-signing` signing policy, with manual approval.
3. Add a SignPath CI user API token as the `SIGNPATH_API_TOKEN` repository
   secret, and the organization ID as the `SIGNPATH_ORGANIZATION_ID`
   repository variable. Set `SIGNPATH_PROJECT_SLUG` or `SIGNPATH_POLICY_SLUG`
   only if you chose different slugs.
4. Remove the unsigned-installer notes from the README, the website,
   [getting started](getting-started.md) and
   [installation and updates](updates.md#if-windows-warns-about-the-download)
   after the first signed release.

### winget

Stable releases can be submitted to the Windows Package Manager by the
[winget workflow](../.github/workflows/winget.yml), which runs
[Komac](https://github.com/russellbanks/Komac) to open a pull request in
`microsoft/winget-pkgs` when a stable release is published. Betas are never
submitted. It stays off until the first version is in winget-pkgs:

1. After a stable release, write its manifests:

   ```powershell
   python scripts/new-winget-manifest.py --version 1.4.0.00 `
     --sidecar medal-sync-1.4.0.00-setup-win-x64.exe.sha256 --out winget
   ```

2. On Windows, run `winget validate` on the version folder, then test it on a
   clean Windows VM with `winget install --manifest` for both `--scope machine`
   and `--scope user`.
3. Copy the `manifests` folder into a fork of `microsoft/winget-pkgs` and open
   a pull request. Answer the review until it is merged.
4. Create a classic GitHub token with only the `public_repo` scope on the
   account that owns the fork, and save it as the `WINGET_TOKEN` repository
   secret. Komac cannot open winget-pkgs pull requests with a fine-grained
   token.
5. Set the repository variable `WINGET_PACKAGE_IDENTIFIER` to
   `LarsonLogan.MedalSync`. From then on, every published stable release opens
   its own winget-pkgs pull request. Choose **Run workflow** with a version to
   resubmit one.
6. Add the `winget install` command to the website, and remove "once it is
   available" from the README, [getting started](getting-started.md) and
   [install with winget](updates.md#install-with-winget).

Komac is pinned by version and SHA-256 in the workflow. Update both together.

Repository visibility, branch/tag rules and vulnerability reporting remain
separate GitHub settings. No build or test deploys onto a live library.

## Beta releases

The [beta workflow](../.github/workflows/beta.yml) publishes every push to
`dev` as a GitHub prerelease of the release that promoting `dev` would publish
next, tagged `vMAJOR.MINOR.PATCH.NN-beta.N`, such as `v1.3.1.02-beta.3`.
[Versions and schemas](versioning.md#betas) explains the numbering. It can also
be started from **Run workflow** on the Actions tab for `dev`; it never builds
another branch.

It works like the release workflow. Runs never overlap, and a queued newer push
replaces an older one; it uses its own concurrency group, so a beta never
replaces a queued release. It does nothing when the commit is no longer the tip
of `dev` or already has a release. Otherwise it passes the beta label to the
full validation workflow, which checks the commit is on `dev`, builds both
packages with that version and no caches and runs every suite. The publish job
then verifies, attests and uploads the tested files to a draft with notes since
the latest stable release, and publishes it with the same app token, marked as a
prerelease and never as Latest. It refuses to publish when that beta, or the
release it leads up to, already exists, so a beta never follows its own
release; choose **Run workflow** on `dev` to take the next number.

This run is `dev`'s only full validation: validation does not run on its own
for pushes to `dev`, so each commit is tested once and a flaky test has one
chance to fail it, not two. A promotion reuses the result of its `Validate beta`
job, even when publishing failed afterwards. A commit whose beta run was
skipped or replaced in the queue by a newer push has no beta validation, so
promoting it runs every suite.
Publishing a beta never closes a
milestone, and the release workflow ignores betas when it chooses the next
hotfix number. Release notes for a stable release start at the previous stable
release, never at a beta. Betas are never deleted automatically.
