# Medal Sync

A Windows companion for [Medal](https://medal.tv) that combines SSD-cached
server storage with one-PC-at-a-time clip library handoff. Each PC keeps its
own login and recording settings. Uploads are verified before the next PC
can take ownership.

**Experimental software for Windows x64.** Keep an independent media backup.
Unknown schemas, uncertain uploads and unreviewed removals stop handoff and
retain ownership and cache. Real reboot and two-PC acceptance remain separate
from automated testing. Medal Sync is an independent project, unaffiliated
with Medal.

## Getting started

Read [setup and the safety model](docs/getting-started.md) before configuring a
live library. Published programs are self-contained; users do not need .NET.
Storage requires rclone, WinFsp, an SSD cache and a reachable SMB server.

[GitHub Releases](https://github.com/Larson-Logan/medal-sync/releases) is the
distribution channel once a maintainer publishes a release. See
[installation, updates and removal](docs/updates.md) for verified downloads.
Every desktop launch checks for newer published releases with a two-second
network budget. Installation happens only when explicitly requested.

## Development

Use Windows, PowerShell 7, .NET SDK 10.0.202 (global.json), Node 24 and
Python 3.11+. No credentials or live Medal library are needed for tests.

```powershell
pwsh -File scripts/validate.ps1
pwsh -File scripts/package-release.ps1
```

Builds and release packages go into ignored `.medal-sync-dist/`. Building a
package does not install it. Windows CI runs on pull requests and main;
release tags produce drafts for maintainer review.

## Project guides

- [Setup and architecture](docs/getting-started.md)
- [Installer, updater, uninstaller and recovery](docs/updates.md)
- [Maintainer release process](docs/releases.md)
- [Contributing](CONTRIBUTING.md), [tests](tests/README.md) and [changelog](CHANGELOG.md)
- [Security](SECURITY.md) and [third-party dependencies](THIRD_PARTY.md)

Licensed under the [MIT license](LICENSE). Dependencies retain their own licenses.
