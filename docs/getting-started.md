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

An unconfigured desktop launch shows the expected configuration path and offers
the native configuration wizard. Open **Medal Sync Setup Guide** in the Start
menu for the full setup steps. An unconfigured sign-in launch exits quietly.
Declining or canceling the wizard leaves configuration and identity untouched.
Repeated sign-in launches also leave an already supervised session alone.

## Choose storage

Use server storage with an SSD cache, keep a persistent local copy that uploads
back to the server, and optionally add verified OneDrive, Dropbox or Google Drive
replicas. See [storage choices and configuration](storage.md) for commands and
multiple-provider examples. SMB remains the shared ownership authority.
Local mode keeps media and coordination on this PC, with optional cloud copies
and no SMB dependency. It does not provide automatic handoff to another PC.

## Optional: let an AI agent configure it

If you use a coding agent with terminal access on this PC (for example Claude
Code or Codex), you can paste the prompt below to have it gather the details,
ask you the remaining questions and prepare `config.json` for you. It follows
the same preview-then-apply rules as the manual steps in the next section. It
stops before anything that creates or joins a library, changes Medal's startup
entries or touches your clips, and shows you those steps to review instead.
Prefer doing it yourself? Skip to the next section.

Do not paste passwords, cloud tokens or Medal login details into the chat. The
agent never needs them: cloud sign-in happens in rclone's own prompt, which you
run yourself.

```text
Help me configure Medal Sync on this Windows PC. Follow
https://github.com/Larson-Logan/medal-sync/blob/main/docs/getting-started.md
and docs/storage.md in the same repository. Work step by step, explain each
command before running it, and ask me whenever a choice is mine.

1. Find the CLI. Check, in order: "$env:ProgramFiles\Medal Sync\bin",
   "$env:LOCALAPPDATA\Programs\Medal Sync\bin" and "$HOME\.medal-sync\bin".
   Use medal-sync.exe from the first folder that has it and tell me which one.
2. Read-only checks: run `compatibility` and `inspect --json`. Report the
   Medal versions, whether the schema is supported, the account ID
   (library.accountId), WinFsp status, existing capture folder and free SSD
   space. Note the stateRoot that inspect reports. If compatibility fails,
   stop and tell me.
3. If config.json already exists in that state root, stop. Show me its
   storage, cache and library settings and do not replace it.
4. Ask me, one at a time:
   - Storage type: server with SSD cache, server plus a persistent local
     copy, local only, or an rclone primary (see docs/storage.md).
   - For server modes: the SMB media share (for example
     \\SERVER\Data\Media\Videos) and the coordination share (for example
     \\SERVER\Data\AppData\MedalSync). Test that both paths are reachable.
   - Whether this is a NEW library or joining an EXISTING one. If joining,
     ask for the library ID from the other PC's config.json.
   - SSD cache folder (for example E:\MedalCache) and, if used, the
     persistent local copy folder (for example E:\MedalLibrary). Both must
     be dedicated folders on a local SSD, outside OneDrive/Dropbox/Google
     Drive/Syncthing folders. Offer to create them.
   - Capture folder (default M:\Clips); confirm the drive letter is free.
   - Optional cloud copies. If I want them, have me run
     `rclone config --config "$HOME\.medal-sync\rclone.conf"` myself and
     sign in; you only need the remote name, such as onedrive:MedalSync.
5. Build the `config create` command with my answers and
   --out "$HOME\medal-sync-candidate.json". Use --new-library only for a
   genuinely new library; use --library-id ID when joining. Run it WITHOUT
   --apply, show me the full preview and wait for my OK. Then run the same
   command with --apply.
6. Run `config validate --config` on the candidate and fix any reported
   field with me. If the stateRoot from step 2 has no config.json, copy the
   validated candidate there as config.json. Never overwrite an existing
   config.json and never copy device.json from another PC.
7. Run `inspect` again and summarise what is configured.
   Then list the remaining steps from getting-started.md for me to do
   (init preview for a new library or enroll preview for a joining PC,
   backups, startup helper), with the exact commands. Do not run anything
   with --apply in those steps.

Rules: never run `init --apply`, `enroll --apply`, `abandon`, `repair`,
`finish --accept-library-changes` or set-medal-startup.ps1 -Apply. Never
force-close Medal, delete SQLite -wal/-shm files, clear the cache, reset or
initialize an existing journal, or edit Medal's database. If any command
refuses or reports a problem, stop, show me its exact output and explain it;
do not work around it. Keep config files, snapshots and logs out of Git and
synced folders, and do not print tokens or credentials.
```

The agent's result is the same `config.json` the wizard or CLI would produce,
so the remaining steps in this guide still apply. Review its summary before
continuing; you can always rerun `config validate` or the configuration wizard
to check its work.

## Plan configuration before using live data

Run `medal-sync-launch.exe --configure`, choose **Configure** from the status
tray menu, or accept the wizard offered by an unconfigured desktop launch.
Choose local storage or server (SMB) storage and explicitly choose a new library
ID or enter the existing ID to join. Supply the account ID from `medal-sync
inspect`, storage/coordination/cache folders, capture drive and folder, and
optional persistent local copy and cloud targets (`name=remote:folder`, one per
line). Local mode needs no persistent-copy field because its primary storage
already stays on this PC. Cloud credentials remain in the separate local rclone
configuration file; authorize providers explicitly with `rclone config` first.

Create the dedicated storage, coordination and cache folders before reviewing.
The wizard checks path rules, Medal compatibility/account, folder availability,
the rclone executable and the SSD free-space reserve. A new library ID must not
already have a coordination directory. Joining checks the existing journal and
committed generation and requires Ready. **Review** shows the resolved state
location, identity, destinations, capture drive, cache limit/reserve, planned
session length, sync exclusions and remaining initialization/enrollment steps.
Press **Confirm and save** only after reviewing them; Enter in the review page
does not save. **Back** discards that review; **Cancel** and Escape leave the
existing setup untouched.

An existing setup displays and preserves its account, library and device
identities. Before changes, both its original journal and the proposed
destination must be Ready and no unfinished local session may remain. Saving
rechecks compatibility, storage and the exact reviewed config/device files,
retains a verified configuration backup in `Backups`, and atomically replaces
the configuration. Changed review state or a linked/reparse state path refuses
saving. Recover missing configuration with an existing device identity from its
original backup instead of creating a new identity. No wizard operation mounts
storage, initializes a journal, enrolls a PC, changes Medal's database or edits
startup entries. Continue with the separate reviewed steps below after saving.

The CLI configuration workflow remains available:

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
server readiness. Missing, null or empty required fields produce an invalid
configuration diagnostic naming the field and exit code 1; correct the candidate
and validate it again. Keep real configuration and identity outside Git.

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

Open **Medal Sync Status** from the Start menu, double-click its notification-area
icon, or run `medal-sync-launch.exe --status`. A configured desktop launch starts
the tray view automatically. Starting, Recording / Active, Uploading, Verifying,
Ready, Waiting for another PC, Recovery needed and Unknown / unreachable are
distinct states. The panel shows available queued files and bytes, the current
required replica, last confirmation time and the next action. Unavailable counts
stay unavailable; an empty queue alone never means Ready. Ready requires the
committed, released journal state after the supervisor's checks.

The view reconnects automatically. Status older than 20 seconds, an unreadable
journal or a lost progress feed shows Unknown; keep this PC's session and cache.
**Retry finish** is available for this PC's interrupted finish after supervision
has stopped. It uses the existing finish checks and never accepts library
removals automatically. Interrupted startup cleanup instead requires reopening
Medal Sync on this PC. A blocked finish shows its diagnostic and required recovery
steps in **Last finish result**; follow those steps before retrying. The last
supervisor refusal stays available when reopening status for that same session.
Waiting for another PC does not start Medal automatically.

The panel and tray menu support keyboard access and native accessibility APIs.
Background notices are coalesced in the panel without popups or taking focus
from games. Closing the panel hides it; **Exit status view** closes only the
view. Supervision, Medal and rclone continue independently. Reopen status at any
time. After Ready, exit the status view before running setup or uninstall, since
those operations require all Medal Sync programs to be closed. Setup guidance
and update checks are also available from the panel. Shutdown closes the view
without interactive prompts while the supervisor keeps its existing finish budget.

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

After a stopped aborted start, its control login and recovery receipt are
retained under `aborted-starts/SESSION/` in this PC's resolved state folder.
The session's cache stays at its original location, even when upload state is
unknown; retained evidence is not a successful-upload receipt. Setup checks
still refuse an active process, unfinished login or non-Ready journal. Run
`finish` on the owning PC to recover an interrupted STARTING session without
launching Medal; a failed stop or evidence write keeps ownership for retry.

An older build may have left an orphan `sessions/SESSION.json` after returning
to Ready. With Medal quit and its clip drive stopped, preview
`recover-start --session SESSION`, then add `--apply` to retain that exact login.
This conservative legacy path requires Ready and an absent or empty session
cache. A nonempty or uncertain older cache refuses and remains blocked for
upload recovery; never delete its login or cache to bypass setup. These files
contain private credentials and paths and must stay local.

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
