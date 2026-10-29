# Getting started

Medal Sync provides an SSD-backed virtual capture drive and shares library
records between Windows PCs in server mode. A shared server journal grants one
PC ownership at a time; local mode keeps media and its journal on one PC.
After Medal quits, the companion waits for uploads, verifies storage
assets, publishes an immutable library generation and releases ownership.
Each PC keeps its own Medal database, login, audio/encoder settings and identity.

## Requirements and limits

- Windows x64, PowerShell 7, Medal, rclone and WinFsp.
- A local SSD with adequate cache and storage space. Server mode also needs an
  SMB share reachable from each PC; local mode needs no SMB server.
- One Medal account and a compatible, explicitly supported database schema.
- An independent media backup. A shared library and cache are not a backup.

Published executables include .NET. The pinned SDK is needed only to build
from source. Run `medal-sync.exe compatibility` (or add `--json`) to check the
installed Medal app, recorder and database schema, even before configuring
Medal Sync. Exit 0 means those compatibility checks pass; exit 1 means a check
failed or could not be completed. This does not test storage or recording.
The supported schema is also reported by `medal-sync.exe version`;
unknown Medal builds or schemas refuse operations. This remains experimental:
real playback, cross-PC handoff and physical reboot need desktop acceptance.

## Install the programs

Follow [installation and updates](updates.md). The setup EXE offers per-user
and all-users installation and creates Start menu shortcuts. All-users setup
defaults to `C:\Program Files\Medal Sync`; per-user setup uses your local
programs folder. Use the location selected in the installer for CLI commands.
It bundles pinned rclone and offers the bundled WinFsp driver in all-users mode.
It does not enroll a library, mount storage or change Medal's startup entries.

Configuration and identity stay in your profile, separate from Program Files.
The managed installation reuses an existing `$HOME/.medal-sync` configuration,
then an older `%LOCALAPPDATA%/LarsonServer/MedalSync` configuration. Fresh
installations use `$HOME/.medal-sync`. Existing portable installations continue
using adjacent state. No identity or library data is copied by setup.

An unconfigured desktop launch shows the expected configuration path and points
to **Medal Sync Setup Guide** in the Start menu. An unconfigured sign-in launch
exits quietly. Neither launch creates configuration or a device identity.
Repeated sign-in launches also leave an already supervised session alone.

## Choose storage

Use server storage with an SSD cache, keep a persistent local copy that uploads
back to the server, and optionally add verified OneDrive, Dropbox or Google Drive
replicas. See [storage choices and configuration](storage.md) for commands and
multiple-provider examples. SMB remains the shared ownership authority.
Local mode keeps media and coordination on this PC, with optional cloud copies
and no SMB dependency. It does not provide automatic handoff to another PC.

## Plan configuration before using live data

The JSON examples under `tools/medal-sync/config/` contain placeholders. The
`192.0.2.10` address is documentation-only; replace it with your server.
Choose a separate disposable spike share to test mounting first. Commands
below are a configuration preview; they do not create a library:

```powershell
# Use your chosen installation location (this example is all-users setup).
$cli = "$env:ProgramFiles/Medal Sync/bin/medal-sync.exe"
& $cli config create --account-id YOUR_ACCOUNT_ID --new-library `
  --cache-root E:\MedalCache --remote '\\YOUR-SERVER\Data\Media\Videos' `
  --coordination-root '\\YOUR-SERVER\Data\AppData\MedalSync' `
  --out "$HOME/medal-sync-candidate.json"
```

Use `--apply` only after reviewing the preview; it creates a new candidate file
and refuses to overwrite an existing file. Validate it with
`config validate --config FILE`. Validation checks syntax and paths, not
server readiness. Keep real configuration and identity outside Git.

After validating the candidate, place it at `config.json` in the resolved user
data folder described above or shown by an unconfigured desktop launch. Create
that folder if needed; do not replace an existing configuration or copy another
PC's `device.json`. The
candidate is a plan, so creating it alone does not configure a desktop launch.

Before `init --apply`, quit Medal normally, preserve a verified database and
media backup, prove all referenced assets exist on the server, verify capture
paths and isolate the SSD cache from file synchronization. `init` without
`--apply` previews server asset checks; initialization is only for a genuinely
new shared library. A second PC uses the same library ID and `enroll` preview
to review its existing records. Never initialize an existing journal.

The new Start menu shortcut opens the companion without replacing Medal's own
startup entries. Existing portable startup integration remains in place. Advanced
per-user integration can be changed using the portable startup helper after
configuration and enrollment have been verified:

```powershell
& "$HOME/.medal-sync/bin/set-medal-startup.ps1" -StateRoot "$HOME/.medal-sync"
# Apply the reviewed changes with the same command plus -Apply.
```

The helper saves originals before changes. Launch through the Medal Library
shortcut so storage and ownership are checked before Medal opens. After exit,
wait for **Ready** before switching PCs, updating or uninstalling.

The desktop launcher passes `medal://` links through the same ownership and
storage checks. A first launch opens the link as part of starting Medal; an
already supervised session receives it immediately. An unmanaged Medal process
must pass recovery checks before receiving the link. Advanced CLI launches can
use `launch --medal-link 'medal://...'` too.

Medal Sync checks compatibility before starting a new session, once a minute
while Medal runs, and before publishing a handoff. It warns about changed
versions and unsupported schemas without stopping Medal or the clip drive.
After a warning, quit Medal normally. Uploads can drain, but an incompatible
build keeps ownership and cache on this PC. Run `compatibility --json`, check
for a Medal Sync update, then retry `finish` after compatibility passes.
Matching database schemas alone do not verify Medal's native backup behavior;
unknown app/recorder builds remain blocked. Enrollment allows the two reviewed
app builds 2643.583.1 and 2644.591.1 with recorder 2643.3040.1 to differ between
PCs when their supported schemas match.

## Recovery rules

Unpublished, uncertain or unreviewed work retains ownership and cache. Read
`status` and recover through `finish`; do not force-kill Medal, remove SQLite
sidecars, clear cache, reset the journal or automatically accept removals.
Removal approval must match the exact reviewed session and snapshot. An
unknown upload state is never success. Device settings and logins stay local.

If startup cannot verify that its clip drive stopped, ownership stays on this
PC. Open Medal Library here again to retry cleanup before switching PCs.

Finish rechecks the library after server verification and drive shutdown; a
change retains the session and cache until a successful retry. Live
`repair --apply` and `relocate --apply` need exclusive access: if the companion
is still supervising a session, they refuse. Wait for its result, then retry
with Medal quit and any recovery mount/cache retained.

Deleting the last clip can publish an empty shared library. Another PC may apply
that generation over its known older committed library, keeping a rollback backup
and its own device settings. Unshared local changes still require explicit review
before replacement.

Audit and repair checkpoints must match this library's account, supported
schema, columns and counter definitions. Mismatches refuse both preview and
apply before planning changes.

Windows shutdown gives the existing finish worker up to 25 seconds. Incomplete
work remains owned and resumes on the next launch. An automatic update check
never installs during shutdown or interrupts this recovery path.
