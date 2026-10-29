# Contributing to Medal Sync

Bug reports, documentation fixes and focused pull requests are welcome. For
setup and usage help, start with [support](SUPPORT.md). Search
[existing issues](https://github.com/Larson-Logan/medal-sync/issues) and
[pull requests](https://github.com/Larson-Logan/medal-sync/pulls) before opening
new work. Discuss substantial storage, ownership or schema changes in an issue
before implementing them so maintainers can assess the recovery implications.

Use the issue chooser for bugs, features, questions or documentation problems.
A useful bug report includes versions, installation method, storage mode,
minimal steps, expected and actual behavior, and the last known session state.
Use synthetic values and describe previous recovery attempts. Never attach real
databases, media, device identities, private configuration or raw runtime logs.
Follow [security reporting](SECURITY.md) for vulnerabilities and the
[code of conduct](CODE_OF_CONDUCT.md) in all project spaces.

## Set up a checkout

Use Windows, PowerShell 7, the .NET SDK pinned in global.json, Node 24 and
Python 3.11+. Fork the repository if you do not have write access, clone your
fork, and create a branch from current `main`. Use one branch per focused change.
See the [test guide](tests/README.md) for the source layout and individual suites.

Tests require no account, server, credentials or recordings. Fixtures are
synthetic; Windows registry and shortcut tests use temporary resources.
Personal assistant settings stay local; see
[personal development tools](docs/local-development.md).

## Validate your change

After adding, removing or renaming files, refresh the public source inventory.
It lists file names only, so edits to existing files never change it:

```powershell
python scripts/update-source-manifest.py
pwsh -File scripts/validate.ps1
```

The generator runs source hygiene first and includes only Git-visible files.
Validation checks the inventory, synthetic tests, builds and local file
links. Personal configuration and runtime files remain excluded. Inspect
`git diff` and `git status --short` before sharing changes.

For documentation and GitHub-template changes, these focused checks are enough;
record that the full Windows suite was not needed:

```powershell
python scripts/update-source-manifest.py
python scripts/update-source-manifest.py --check
node scripts/check-doc-links.mjs README.md CONTRIBUTING.md SUPPORT.md CODE_OF_CONDUCT.md docs/triage.md
git diff --check
```

Include every Markdown file you changed in the link check. It verifies local
file targets; remote URLs and heading anchors still need review. Check edited
GitHub YAML against the relevant GitHub documentation as well.

Runtime, dependency and tooling changes need the standalone Windows suite.
Changes to ownership, upload verification, schema support or database restore
need tests covering refusal and recovery as well as success. Unknown states
must fail closed. See [getting started](docs/getting-started.md) for the safety
model. Keep package lock files current and use locked restores. The shared
version is in Directory.Build.props; record user-visible changes in CHANGELOG.md.
A change to a file `format` or to supported Medal schemas follows
[versions and schemas](docs/versioning.md).

Build the setup EXE and portable ZIP when changing packaging:

```powershell
pwsh -File scripts/package-windows-installer.ps1
```

The first setup build installs the pinned, signed Inno Setup compiler into
ignored `.validation/build-tools/` for the current developer. Publishing
binaries and installing onto a live library are separate actions. CI cannot
establish real reboot, playback, provider sign-in or two-PC acceptance.

## Open and review a pull request

Target `main` and use a title that describes the resulting behavior. Explain
the problem, link related issues and list the commands and results you checked.
Use `Closes #123` only when the change fully resolves that issue; merged pull
requests and the issues they close join the next release's milestone
automatically, and maintainers handle milestones and the
[project board](docs/triage.md#project-board). Explain
failure and recovery behavior for stateful changes, and any compatibility or
upgrade effects. Small documentation fixes do not need invented runtime tests.

Open a draft while work or required validation remains incomplete. If you
cannot run a check, state why and what still needs to run. A queued, skipped
or infrastructure-blocked GitHub job is not a passing check. Fork contributions
may need maintainer approval or a reviewed validation path; see
[maintainer triage](docs/triage.md).

Maintainers review scope, privacy, failure behavior and validation before
merging. Respond to requested changes in the same PR and record any new test
results. Dependency updates receive the same review; major upgrades may require
a separate migration. There is no guaranteed response time or automatic merge.

Contributions are licensed under the repository's [MIT license](LICENSE).
