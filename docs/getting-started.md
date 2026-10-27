# Getting started

Medal Sync provides an SSD-backed virtual capture drive and shares library
records between Windows PCs. A server journal grants one PC ownership at a
time. After Medal quits, the companion waits for uploads, verifies server
assets, publishes an immutable library generation and releases ownership.
Each PC keeps its own Medal database, login, audio/encoder settings and identity.

## Requirements and limits

- Windows x64, PowerShell 7, Medal, rclone and WinFsp.
- An SMB share reachable from each PC and a local SSD with adequate cache space.
- One Medal account and a compatible, explicitly supported database schema.
- An independent media backup. A shared library and cache are not a backup.

Published executables include .NET. The pinned SDK is needed only to build
from source. The supported schema is reported by `medal-sync.exe version`;
unknown Medal builds or schemas refuse operations. This remains experimental:
real playback, cross-PC handoff and physical reboot need desktop acceptance.

## Install the programs

Follow [installation and updates](updates.md). Install into an explicit per-user
directory such as `$HOME/.medal-sync`. The installer copies programs only;
it does not enroll a library, mount storage, change startup or start Medal.

Configuration and device identity live in the state root, with executables
under its `bin` directory. Once both `config.json` and `device.json` exist,
the installed programs use that adjacent state root. Otherwise they fall back
to `%LOCALAPPDATA%/LarsonServer/MedalSync`. Establish initial state in that
default location, then move the stopped state as a unit to a custom location
before rewiring startup. Never run a partially configured custom installation.

## Plan configuration before using live data

The JSON examples under `tools/medal-sync/config/` contain placeholders. The
`192.0.2.10` address is documentation-only; replace it with your server.
Choose a separate disposable spike share to test mounting first. Commands
below are a configuration preview; they do not create a library:

```powershell
$cli = "$HOME/.medal-sync/bin/medal-sync.exe"
& $cli config create --account-id YOUR_ACCOUNT_ID --new-library `
  --cache-root E:\MedalCache --remote '\\YOUR-SERVER\Data\Media\Videos' `
  --coordination-root '\\YOUR-SERVER\Data\AppData\MedalSync' `
  --out "$HOME/medal-sync-candidate.json"
```

Use `--apply` only after reviewing the preview; it creates a new candidate file
and refuses to overwrite an existing file. Validate it with
`config validate --config FILE`. Validation checks syntax and paths, not
server readiness. Keep real configuration and identity outside Git.

Before `init --apply`, quit Medal normally, preserve a verified database and
media backup, prove all referenced assets exist on the server, verify capture
paths and isolate the SSD cache from file synchronization. `init` without
`--apply` previews server asset checks; initialization is only for a genuinely
new shared library. A second PC uses the same library ID and `enroll` preview
to review its existing records. Never initialize an existing journal.

After configuration and enrollment are verified, the startup helper previews
per-user shortcut and startup changes:

```powershell
& "$HOME/.medal-sync/bin/set-medal-startup.ps1" -StateRoot "$HOME/.medal-sync"
# Apply the reviewed changes with the same command plus -Apply.
```

The helper saves originals before changes. Launch through the Medal Library
shortcut so storage and ownership are checked before Medal opens. After exit,
wait for **Ready** before switching PCs, updating or uninstalling.

## Recovery rules

Unpublished, uncertain or unreviewed work retains ownership and cache. Read
`status` and recover through `finish`; do not force-kill Medal, remove SQLite
sidecars, clear cache, reset the journal or automatically accept removals.
Removal approval must match the exact reviewed session and snapshot. An
unknown upload state is never success. Device settings and logins stay local.

Windows shutdown gives the existing finish worker up to 25 seconds. Incomplete
work remains owned and resumes on the next launch. An automatic update check
never installs during shutdown or interrupts this recovery path.
