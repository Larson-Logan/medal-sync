# Contributing to Medal Sync

Use Windows, PowerShell 7, the .NET SDK pinned in global.json, Node 24 and
Python 3.11+. Run `pwsh -File scripts/validate.ps1` to validate and
`pwsh -File scripts/package-windows-installer.ps1` to build the verified setup
EXE and portable ZIP. The first build installs the pinned, signed Inno Setup
compiler into ignored `.validation/build-tools/` for the current developer.

Tests require no account, server, credentials or recordings. Fixtures are
synthetic; Windows registry and shortcut tests use temporary resources.
Personal assistant settings stay local; see [personal development tools](docs/local-development.md).
Keep package lock files current and use locked restores. The shared version
is in Directory.Build.props; record user-visible changes in CHANGELOG.md.

Explain the problem, resulting behavior and validation in each pull request.
Changes to ownership, upload verification, schema support or database restore
need tests covering refusal and recovery as well as success. Unknown states
must fail closed. See [the safety model](docs/getting-started.md).

Never attach real databases, media, device identities, private configuration
or raw runtime logs to an issue. Use synthetic examples. See
[security reporting](SECURITY.md) for vulnerabilities.

Publishing binaries and installing onto a live library are separate actions.
CI cannot establish real reboot, playback or two-PC acceptance.
