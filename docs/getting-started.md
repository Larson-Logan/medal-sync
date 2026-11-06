# Getting started

Medal Sync provides an SSD-backed virtual capture drive and shares library
records between Windows PCs in server mode. A shared server journal grants one
PC ownership at a time; local mode keeps media and its journal on one PC.
After Medal quits, the companion waits for uploads, verifies storage
assets, publishes an immutable library generation and releases ownership.
Each PC keeps its own Medal database, login, audio/encoder settings and identity.

## Quick start

Most people keep their clips on a NAS or home server and share them between
two or more PCs. This is the **server with SSD cache** mode. If that is you,
follow these steps on each PC, then read only the sections they link to. Not
sure that is your setup? See [which mode should I pick?](#which-mode-should-i-pick)
first.

1. **Check you have what's needed:** Windows x64, Medal signed in, a local SSD
   and an SMB share every PC can reach. See
   [requirements and limits](#requirements-and-limits).
2. **Back up first.** Copy your clips and quit Medal before changing anything.
   A shared library is not a backup.
3. **Install.** Download the setup EXE from
   [Releases](https://github.com/Larson-Logan/medal-sync/releases/latest) and
   choose all-users setup, so it can install the WinFsp driver. See
   [install the programs](#install-the-programs).
4. **Make the folders.** On the server, a media share (for example
   `\\SERVER\Data\Media\Videos`) and a coordination folder (for example
   `\\SERVER\Data\AppData\MedalSync`). On this PC, an empty cache folder on the
   SSD (for example `E:\MedalCache`).
5. **Configure.** Open **Medal Sync** from the Start menu and accept the
   configuration wizard, or run `medal-sync-launch.exe --configure`. Choose
   **Server (SMB)**. On the first PC choose a new library; on every other PC
   enter the first PC's library ID. Review, then save. See
   [plan configuration](#plan-configuration-before-using-live-data).
6. **Create or join the library.** On the first PC run `medal-sync init`, read
   the preview, then `medal-sync init --apply`. On every other PC run
   `medal-sync enroll --out DIR` and then the same command with `--apply`.
   Never run `init` on a library that already exists.
7. **Record.** Open Medal from the **Medal Library** shortcut. After you quit
   Medal, wait for **Ready** in the status panel before switching PCs. When
   it all works, you can choose **Start Medal at sign-in** in the tray menu.

Everything below explains each step in detail, along with recovery and
advanced options.

### Which mode should I pick?

Answer from the top and stop at the first yes:

- **One PC only, and no server?** Use
  [local storage](storage.md#local-storage-and-cloud-without-a-server). Cloud
  copies are optional.
- **Several PCs and a NAS or home server (SMB)?** Use server with SSD cache
  (the quick start above).
- **Same, but you also want every clip on each PC's own disk?** Use
  [a local copy and the server](storage.md#keep-a-local-copy-and-upload-to-the-server).
- **Several PCs and Google Drive, but no server?** Use
  [Google Drive without a server](storage.md#google-drive-without-a-server).
- **Clips on a cloud provider or SFTP server, with SMB still used for handoff?**
  Use [an rclone primary](storage.md#use-a-cloud-provider-or-personal-server-as-primary-storage).

Any mode can also copy clips to OneDrive, Dropbox or Google Drive; see
[add OneDrive, Dropbox or Google Drive](storage.md#add-onedrive-dropbox-or-google-drive).
The [storage guide](storage.md) compares every mode.

## Requirements and limits

- Windows x64, PowerShell 7, Medal, rclone and WinFsp.
- A local SSD with adequate cache and storage space, formatted NTFS, ReFS or
  exFAT (see [drives and file systems](storage.md#choose-drives-and-file-systems)).
  Server mode also needs an SMB share reachable from each PC; local mode needs
  no SMB server.
- One Medal account and a compatible, explicitly supported database schema.
- An independent media backup. A shared library and cache are not a backup.

Published executables include .NET. The pinned SDK is needed only to build
from source. Run `medal-sync.exe compatibility` (or add `--json`) to check the
installed Medal app, recorder and database schema, even before configuring
Medal Sync. Exit 0 means those compatibility checks pass; exit 1 means a check
failed or could not be completed. This does not test storage or recording.
The supported schema is also reported by `medal-sync.exe version`;
unreadable version metadata or unknown schemas refuse operations. Version
numbers alone do not block a routine Medal update. This remains experimental:
real playback, cross-PC handoff and physical reboot need desktop acceptance.

## Install the programs

Follow [installation and updates](updates.md). The setup EXE is not
code-signed yet, so Windows may warn about an unknown publisher; check the
download's SHA-256 first, then choose **More info**, then **Run anyway** (see
[if Windows warns about the download](updates.md#if-windows-warns-about-the-download)).
Once it is available, you can [install with winget](updates.md#install-with-winget)
instead. The setup EXE offers per-user
and all-users installation and creates Start menu shortcuts. All-users setup
defaults to `C:\Program Files\Medal Sync`; per-user setup uses your local
programs folder. Use the location selected in the installer for CLI commands,
or select **Add medal-sync to PATH** in setup to type `medal-sync` in any new
terminal.
It bundles pinned rclone and offers the bundled WinFsp driver in all-users mode.
It does not enroll a library, mount storage or change Medal's startup entries.

Configuration and identity stay in your profile, separate from Program Files.
The managed installation reuses an existing `$HOME/.medal-sync` configuration,
then an older `%LOCALAPPDATA%/MedalSync` or `%LOCALAPPDATA%/LarsonServer/MedalSync`
configuration. Fresh installations use `$HOME/.medal-sync`; see
[where settings are read from](commands.md#where-settings-are-read-from). Existing portable installations continue
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
multiple-provider examples. Server mode uses SMB as the shared ownership authority.
For cross-PC handoff using only Google Drive, follow
[Google Drive without a server](storage.md#google-drive-without-a-server).
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
     Drive/Syncthing folders, on NTFS, ReFS or exFAT (not FAT32, a RAM disk
     or, for the cache, a network drive). Offer to create them.
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
`finish --accept-library-changes`, `startup --apply` or set-medal-startup.ps1 -Apply. Never
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
Choose server (SMB), local or Google Drive storage; the wizard then shows only
the fields that mode uses, each with a short explanation and example, and
ignores values in hidden fields. Explicitly choose a new library ID or enter the
existing ID to join. A new setup fills in the Medal account ID from the account
signed in on this PC (it reads only the library file name); if none is found,
sign in to Medal and choose **Detect**, or enter the ID from `medal-sync
inspect`. Supply the storage, coordination and cache folders (**Browse...**
picks folders and the rclone/credential files), capture drive and folder, and
optional persistent local copy and cloud targets (`name=remote:folder`, one per
line). Prefix a target with `required ` to require verification before handoff,
or `optional ` to allow retry later. Existing targets show their requirement;
keeping a name without a prefix keeps its saved requirement. Review lists each
requirement and calls out changed requirements and added or removed targets.
Local mode needs no persistent-copy field because its primary storage
already stays on this PC. Cloud credentials remain in the separate local rclone
configuration file; authorize providers explicitly with `rclone config` first.

Create the dedicated storage, coordination and cache folders before reviewing.
The wizard checks path rules, Medal compatibility/account, folder availability,
the rclone executable, the provider credential file for cloud targets or an
rclone primary, the file system and drive type of the cache and persistent
copy, and the SSD free-space reserve on the disk that holds the cache. A new library ID must not
already have a coordination directory. Joining checks the existing journal and
committed generation and requires Ready. **Review** shows the resolved state
location, identity, destinations, capture drive, cache limit/reserve, planned
session length, sync exclusions and remaining initialization/enrollment steps.
Session hours accept hundredths, including `0.5` and `2.5`. If a saved number
cannot be displayed exactly or exceeds a field's display range, the wizard
shows a message and keeps the original value unless you edit that field.
Review shows the actual values that will be saved.
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

Every `config create` option is listed in the [command reference](commands.md#config-create-options),
and every field it writes in the [configuration reference](configuration.md).
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
This works with an SMB primary and with an rclone primary such as
`onedrive:MedalSync`. With an rclone primary, `enroll --apply` copies the PC's
clips into its local copy, and the finish that follows uploads and verifies
them before the library is shared or released. See
[enroll into an rclone primary](storage.md#enroll-a-pc-with-existing-clips-into-an-rclone-primary).

The new Start menu shortcut opens the companion without replacing Medal's own
startup entries. Setup never changes them. After configuration and enrollment
have been verified, choose **Start Medal at sign-in** in the Medal Sync tray
menu to start Medal through Medal Sync whenever you sign in. It lists exactly
what changes and asks first. The same change is available from a terminal:

```powershell
medal-sync startup            # preview; changes nothing
medal-sync startup --apply    # start Medal through Medal Sync at sign-in
medal-sync startup --restore  # put Medal's own startup entries back
```

Turning it on saves Medal's original Run value, `medal://` handler and
shortcuts first (`startup-originals.json` in your state folder), and the
launcher keeps them pointed at Medal Sync after Medal updates. Turning it off
puts the originals back. If Windows has Medal Sync turned off in **Settings >
Apps > Startup**, Medal Sync reports it and leaves that switch to you. Portable
installs can still use `set-medal-startup.ps1` from their `bin` folder; both
use the same saved originals. Launch through the Medal Library
shortcut so storage and ownership are checked before Medal opens. After exit,
wait for **Ready** before switching PCs, updating or uninstalling.

Open **Medal Sync Status** from the Start menu, double-click its notification-area
icon, or run `medal-sync-launch.exe --status`. A configured desktop launch starts
the tray view automatically. Starting, Medal open, Recording, Uploading, Verifying,
Ready, Waiting for another PC, Recovery needed and Unknown / unreachable are
distinct states. Recording means Medal is writing its replay buffer for a game;
Medal open means it is running without recording (if Medal keeps its replay
buffer in memory, it always shows as Medal open). The notification-area icon
carries a colored dot for the state: green for Ready, red while recording,
purple while Medal is open but not recording, blue while starting, uploading or
verifying, gray while waiting for another PC or for storage, and amber when
recovery is needed or the state is unknown. Hover over it for the state's name.
The panel shows available queued files and bytes, the current
required replica, last confirmation time and the next action. Unavailable counts
stay unavailable; an empty queue alone never means Ready. Ready requires the
committed, released journal state after the supervisor's checks.

Failed and incomplete cloud-copy checks appear in text status and as a panel
notice when no higher-priority notice is showing. Missing or different files
show counts; an incomplete sign-in check says to sign in again. A passing check
or a later handoff that verifies that target clears its warning. Text/JSON
status and diagnostics retain these local results while shared storage is offline.

The view reconnects automatically. Status older than 20 seconds, an unreadable
journal or a lost progress feed shows Unknown; keep this PC's session and cache.
**Retry finish** is available for this PC's interrupted finish after supervision
has stopped. It uses the existing finish checks and never accepts library
removals automatically. Interrupted startup cleanup instead requires reopening
Medal Sync on this PC. A blocked finish shows its diagnostic and required recovery
steps in **Last finish result**; follow those steps before retrying. The last
supervisor refusal stays available when reopening status for that same session.
When you open Medal while another PC owns the library, Medal Sync sends a switch
request and asks whether to **wait and open Medal when ready**. No is the
default. If that PC has already quit Medal and is only uploading, the message
says it hands off by itself; nobody needs to go to it. Yes shows a waiting window that names the PC that has the library,
when it was last checked and whether the server can be reached. Medal opens by
itself once that PC finishes its handoff, after the usual startup checks. Cancel,
Windows shutdown or 15 minutes without a handoff stop the wait and withdraw the
request. An unreachable server or a quiet owner never counts as a handoff. If two
PCs wait, only one gets the library; the other keeps waiting. Choosing to wait
reuses the request just sent, so the other PC is told once. At sign-in nothing
is offered and no request is sent: Windows started Medal, not you, so the PC
that has the library is not interrupted. Open Medal when you want to switch.
Scripts can use `launch --wait-for-handoff SECONDS`. When another PC asks
to switch, the panel shows one notice per request, and while Medal is open the
status names every PC still waiting. A PC disappears from it as soon as it
stops waiting. A request older than six
hours no longer prompts; `status` lists it as old until the next session on the
owning PC removes it. If supervision stops while Medal is still open, its last
message also appears in the panel instead of a dialog that would take focus.

The panel and tray menu support keyboard access and native accessibility APIs.
Background notices are coalesced in the panel without popups or taking focus
from games. Closing the panel hides it. To exit Medal Sync, quit Medal normally,
wait for Ready, then choose **Exit Medal Sync** in the panel or tray menu.
Exit keeps the view open with guidance while supervision or a library operation
is active. Reopen status at any time. Setup and uninstall require all Medal Sync
programs to be closed. Setup guidance and update checks are also available from
the panel; accepting an update waits for you to quit Medal and for Ready, then
installs it and reopens the status panel.
Shutdown closes the view
without interactive prompts while the supervisor keeps its existing finish budget.

### Status for scripts

`medal-sync status --json` prints one JSON document for scripts, dashboards and
Stream Deck buttons. It only reads: it never creates this PC's identity, writes
a switch request or starts anything. Exit code 0 means status was read, 3 means
the shared library could not be reached (a JSON body is still printed), and 1
is a refusal such as a different library's marker or a damaged journal, with
the reason on standard error.

| Field | Meaning |
| --- | --- |
| `format` | `1`. Later versions of format 1 only add fields; see [versions and schemas](versioning.md). |
| `libraryId` | This PC's configured library. |
| `storage` | `"reachable"` or `"unreachable"`; `message` explains the second. |
| `journal` | `null` when unreachable. Otherwise `state` (`CLEAN`, `STARTING`, `ACTIVE`, `DRAINING`, `PUBLISHING`, `RECOVERY_REQUIRED`), `owner` (display name or `null`), `ownedByThisPc`, `sessionId`, `committedGeneration`, `revision`, `updatedAt` and `reason`. |
| `phase`, `title`, `nextAction` | The same phase, title and next step as the tray (`READY`, `STARTING`, `ACTIVE`, `UPLOADING`, `VERIFYING`, `WAITING`, `RECOVERY_NEEDED`, `UNKNOWN`, `WAITING_FOR_STORAGE`). A stale or unreadable progress feed is `UNKNOWN`, never `READY`. |
| `safeToSwitch` | `true` only when the journal is `CLEAN` and the phase is `READY`. |
| `handoffRequests` | `null` when unreachable. Each has `device`, `requestedAt`, `ageSeconds` and `stale` (older than six hours, no longer prompting). |
| `pendingCloudCopies` | Optional cloud copies waiting for a retry: `name`, `firstFailure`, `lastAttempt`. |
| `cloudCopies` | Configured cloud copies: `name`, `lastCheck`, `lastComplete` and `needsAttention`. Each check has `result` (`passed`, `failed`, `incomplete`), `reason` (or `null`), `finishedAt`, `generation`, and counts `planned`, `checked`, `matched`, `missing`, `mismatched`, `unknown`, `bytes`. A check is `null` when unavailable or never run. `lastComplete` preserves the latest completed check if a newer attempt is incomplete. A later verified handoff clears `needsAttention` without rewriting the historical check. |
| `lastVerifiedAt` | When this PC last verified primary storage and its cloud copies, or `null`. |
| `replicaRetry` | Ready-state cloud retry progress/result, or `null`: `generation`, `running`, `message`, `confirmedAt`, optional `target`, plus format and library identity. Stale running evidence becomes an explicit stale result. This does not change library ownership. |
| `observedAt` | When this report was made. |

Times are UTC in ISO 8601. The document has no device IDs, credentials or file
paths; owner and requesting PCs appear by display name only.

### Medal links

The desktop launcher passes `medal://` links through the same ownership and
storage checks. A first launch opens the link as part of starting Medal; an
already supervised session receives it immediately. An unmanaged Medal process
must pass recovery checks before receiving the link. Advanced CLI launches can
use `launch --medal-link 'medal://...'` too.

## Medal updates and compatibility

Medal Sync checks compatibility before starting a new session, once a minute
while Medal runs, and before publishing a handoff. Readable app and recorder
version changes continue automatically when the supported schema still matches.
It warns about unreadable update metadata and unsupported schemas without
stopping Medal or the clip drive.
After a warning, quit Medal normally. Uploads can drain, but an incompatible
build keeps ownership and cache on this PC. Run `compatibility --json`, check
for a Medal Sync update, then retry `finish` after compatibility passes.
Every launch still requires Medal quit and checks its native backup for pending
sidecars, supported schema, integrity and stable lineage before starting Medal.
Library rows and local device settings are verified unchanged when aligning
lineage. These checks do not prove that a future Medal build with the same
schema has unchanged internal backup behavior; automatic continuation accepts
that remaining compatibility risk. Keep an independent backup.

`compatibility --json` reports `buildReviewed` separately from `compatible`.
An unreviewed version pair is informational and does not require approval.
The individually reviewed pairs are app
2643.583.1 or 2644.591.1 with recorder 2643.3040.1, and app 2644.604.1 with
recorder 2644.3209.1. Enrollment allows different readable app and recorder
versions when their supported schemas match exactly.

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

When a session finishes, its uploads are verified and its generation committed
before its SSD cache and login are removed. The login first moves to
`finished-sessions/SESSION/` beside a receipt naming that generation. If a
program such as antivirus, the search indexer or a backup client holds a cached
clip, the delete fails, but what is left does not block setup, the
configuration wizard or the portable updater. The next launch or finish
retries it, and `recover-start --session SESSION --apply` retries it on demand.

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
