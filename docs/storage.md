# Storage choices

Medal Sync supports primary storage on an SMB server or any named rclone remote
(cloud or a personal server), a persistent local media collection, and cloud
replicas. Server mode shares one-PC-at-a-time library ownership through the SMB
journal. Google Drive cloud mode keeps ownership, library generations and media
in Drive and needs no SMB server. Local mode keeps its media and journal on one
PC; replicas send its media to cloud storage.

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed virtual drive, uploaded to SMB | Server media; finished session cache is removed |
| Local copy and server | Virtual drive backed by a persistent local folder | Complete referenced media on the local disk and server |
| Local storage, with optional cloud copies | Virtual drive backed by a persistent local folder | Local media and optional verified cloud copies; no SMB dependency |
| Cloud or personal-server primary | Virtual drive backed by a persistent local folder | Local media and the rclone primary; journal stays on the server |
| Google Drive cloud mode | Virtual drive backed by a persistent local folder | Local media, verified Drive media and library history; ownership uses the Drive API |
| Either mode with cloud replicas | Same recording path as above | Media copied and verified at each reachable cloud destination; offline ones retry later |

The local collection and VFS cache are separate. `localCopyRoot` names a
dedicated storage root, for example `E:\MedalLibrary`; with capture folder
`M:\Clips`, persistent files live in `E:\MedalLibrary\Clips`. Never place
either folder in a provider's desktop sync folder. Provider access uses rclone
directly. Allow disk space for the collection, cache and new recordings.

## Choose drives and file systems

Free space is measured on the disk that actually holds each folder, not on its
drive letter. A cache on a disk mounted inside a folder (for example
`C:\Disks\Fast\MedalCache`) is measured on that disk. A `subst` letter or a
second letter for the same disk is recognized as that disk, so a cache and
persistent copy that share it must both fit in its free space.

External USB disks work for the cache and the persistent copy. If the disk that
holds either one is unplugged, launch reports *Waiting for storage* and changes
nothing. Reconnect it and open Medal again.

The setup wizard and launch refuse these, and `inspect` lists them as blockers:

| Folder | Refused on | Why |
| --- | --- | --- |
| Cache | Network drive letters, RAM disks, optical drives | New recordings exist only in the cache until they upload. |
| Persistent copy | RAM disks, optical drives | The collection must survive a restart. |
| Cache or persistent copy | FAT or FAT32 | Files of 4 GiB or more do not fit, and long recordings can be larger. Use NTFS, ReFS or exFAT. |

A persistent copy, or local mode's storage folder, may be on a mapped network
drive, but recording then depends on that network. For a NAS, prefer server
mode with a UNC share. Persistent copies and storage folders must not pass
through a symbolic link, junction or folder-mounted disk; give them the disk's
own drive letter.

Before downloading clips into the persistent copy, the companion checks that
its disk is connected and suitable and adds up the clips' size. If the download
would leave less than the minimum free space (`mount.minFreeGiB`) on that disk,
launch refuses and downloads nothing.

### Low disk space while recording

While Medal records, Medal Sync checks the free space on the cache's disk and
the persistent copy's disk every 30 seconds and shows a notice when it changes:

- **Low disk space** when free space falls to the warning level. By default
  that is twice `mount.minFreeGiB`, and at least 10 GiB above it. Set
  `mount.spaceWarningGiB` to choose another level above `mount.minFreeGiB`.
  The notice includes a rough time left at Medal's current bitrate.
- **At the reserve** when free space falls to `mount.minFreeGiB` (at least
  1 GiB). Medal may soon fail to save clips; free up space on that disk.
- **Back to normal** once free space is clearly above the warning level again.

A cache and persistent copy on one disk get one notice. A disk that hovers
near a level is announced once, and the same notice is not repeated within 30
minutes. If the free space cannot be read three times in a row (an unplugged or
very slow disk), a notice says it could not be checked; that is never treated
as enough space. These are warnings only: Medal Sync never deletes clips that
have not been copied yet and never closes Medal. During Windows shutdown the
notices are logged without being shown.

### Limit upload speed while recording

In server mode the clip drive uploads new clips while Medal records. Over a VPN
or tunnel, those uploads can compete with the game. Limit them in the `mount`
settings:

```json
"mount": {
  "bandwidthLimitMiB": 5,
  "finishBandwidthLimitMiB": null
}
```

- `mount.bandwidthLimitMiB` applies while Medal runs.
- `mount.finishBandwidthLimitMiB` applies once Medal exits and uploads finish.
  Leave it `null` to finish at full speed, or set the same value as
  `bandwidthLimitMiB` to keep one limit throughout.

Each is 1 to 10000 MiB/s, or `null` (the default) for unlimited. The limit
moves to the finishing value when Medal exits, and back if Medal reopens before
the switch completes. If the drive does not accept a change, a notice says so
and the session continues at the previous speed; the limit never blocks
recording or finishing. `mount-plan` shows the recording limit as `--bwlimit`,
for example `5Mi:off`: the limit applies to uploads only, so playing clips
from the server is never slowed by it.

A limit only changes speed. Uploads take longer, so more clips wait on the SSD
cache, but launch already reserves room for a whole session as if nothing
uploads, and the low disk space notices above still apply. Drain, verification
and ownership work the same way. Cloud copies use `replication.bandwidthLimitMiB`
instead.

### Play clips away from the server

Playing a clip that is not in the SSD cache reads it from the server. The clip
drive fetches up to 256 MiB ahead of the player into the cache, so most clips
download completely soon after playback starts and a slow or distant link
(a VPN, tunnel or another network) does not run out after a few seconds.
Played clips stay in the cache for `mount.cacheMaxAgeSeconds` after they are
closed. If the connection to the server is slower than the clip's bitrate,
let the clip load for a moment before playing, or play it again once cached.

## Keep a local copy and upload to the server

Add `--local-copy-root` to the configuration workflow in
[getting started](getting-started.md):

```powershell
$cli = "$env:ProgramFiles/Medal Sync/bin/medal-sync.exe"
& $cli config create --account-id YOUR_ACCOUNT_ID --new-library `
  --cache-root E:\MedalCache --local-copy-root E:\MedalLibrary `
  --remote '\\YOUR-SERVER\Data\Media\Videos' `
  --coordination-root '\\YOUR-SERVER\Data\AppData\MedalSync' `
  --out "$HOME/medal-sync-candidate.json"
```

This previews configuration; add `--apply` to create the candidate. Reuse
`--library-id ID` when configuring another PC. Review configuration while
Ready and retain the original file before adopting a candidate.

Before Medal starts, the companion copies and verifies the committed
generation's referenced media that the server holds into the local collection.
Clips that only this PC holds are uploaded and verified at finish.
The virtual drive then writes to that persistent collection. After Medal
quits and VFS writes drain, the companion copies current referenced media
to SMB, verifies it byte for byte, and checks server assets before releasing
ownership. Completed-session cleanup removes only the VFS cache; the local
collection stays. Initial hydration can take time for a large library.

The SMB journal must be reachable to acquire ownership. This is not an
offline multi-writer mode. A server outage during recording leaves local
work in place; recovery and handoff wait for the server to return.

## Use a cloud provider or personal server as primary storage

`--remote` (JSON `remote`) accepts either a UNC share or a named rclone remote
and dedicated folder, for example `onedrive:MedalSync`, `gdrive:Medal`,
`r2:medal-sync` or `sftp-home:Media/Videos`. Configure the remote as shown in
the next section. An rclone primary requires `--local-copy-root` and
`--rclone-config`: Medal always records to the persistent local copy, and the
companion uploads and verifies byte for byte before handoff, exactly as for an
SMB primary. `--coordination-root` must still be the SMB share, because cloud
folders cannot provide the ownership lock.

```powershell
& $cli config create --account-id YOUR_ACCOUNT_ID --library-id YOUR_LIBRARY_ID `
  --cache-root E:\MedalCache --local-copy-root E:\MedalLibrary `
  --remote sftp-home:Media/Videos --rclone-config "$HOME/.medal-sync/rclone.conf" `
  --coordination-root '\\YOUR-SERVER\Data\AppData\MedalSync' `
  --out "$HOME/medal-sync-candidate.json"
```

Remote names can be any name rclone accepts, such as `my nas` or
`onedrive.work`. Quote a name that contains spaces on the command line. On an
SFTP or FTP server, a folder without a leading `/` is relative to the account's
home directory; start it with `/` for an absolute path, for example
`sftp-home:/srv/media/Medal`.

For Google Drive without an SMB journal, use the cloud mode below.

## Google Drive without a server

Use `--storage-mode cloud` to switch the same library between PCs using only
Google Drive. Each PC records to its persistent local copy. Drive holds the
media, normalized library generations and an ownership journal addressed by a
shared file ID. Ownership changes use the Drive v2 metadata ETag with
`If-Match`; the companion first checks that the endpoint rejects a mismatching
ETag. It refuses ownership if conditional writes are unavailable. File names,
desktop sync status, timeouts and cached Ready state never grant ownership.

Authorize a **direct Google Drive OAuth remote** with rclone as described below.
Use the same Google account or share the dedicated folder and journal with the
other PC. Aliases, encrypted wrappers, service accounts and other providers
cannot host this journal. Use your own OAuth client as recommended in
[rclone's Google Drive setup](https://rclone.org/drive/#making-your-own-client-id).
Keep the credential file, local copy, cache and downloaded history outside
provider desktop sync folders. Each PC keeps its own device identity.

On the first PC, allocate one journal ID. This reserves an ID without creating
a Drive file; **reuse it on every PC**:

```powershell
$cli = "$env:ProgramFiles/Medal Sync/bin/medal-sync.exe"
$credentials = "$HOME/.medal-sync/rclone.conf"
$journal = & $cli storage cloud-id --remote gdrive:MedalSync --rclone-config $credentials
& $cli config create --storage-mode cloud --new-library --account-id YOUR_ACCOUNT_ID `
  --remote gdrive:MedalSync --cloud-journal-id $journal `
  --local-copy-root E:\MedalLibrary --cache-root E:\MedalCache `
  --rclone-config $credentials --out "$HOME/medal-sync-candidate.json"
```

Review the preview, then repeat `config create` with `--apply`. Adopt the
candidate through the [configuration workflow](getting-started.md#plan-configuration-before-using-live-data)
or use the native configuration wizard's **Google Drive (no server)** mode.
The journal ID field is shown in the review. The local `coordinationRoot`
defaults to the state directory's `coordination` folder; it caches downloaded
immutable history and is never the ownership authority.

Quit Medal. On the first PC, preview `medal-sync relocate --from ORIGINAL_CAPTURE_FOLDER`,
then apply it with `--apply`. This copies referenced files into the persistent
local copy and rewrites library references to the configured virtual capture
folder; original files and a database backup remain. Preview `medal-sync init`,
then run `medal-sync init --apply`. Initialization uploads and verifies the
media and generation 0 before creating the pre-generated Drive journal.
An interrupted upload can be retried with the exact same local snapshot and
device. An existing journal is never reinitialized.

On the other PC, configure `--library-id` with the first PC's ID, the same
`--cloud-journal-id`, Drive folder and capture folder. Choose separate local
media/cache/history folders and authorize rclone on that PC. **Do not run init
or enroll again.** Launch Medal through Medal Sync. An empty library joins
automatically; existing unshared clips refuse replacement and remain protected.
Export and preserve them before choosing the existing explicit
`launch --replace-local-library` workflow. There is no automatic union of
unshared libraries in cloud mode.

Launch restores only library tables, backs up the database, and updates the
capture-folder setting to the configured virtual drive when necessary; other
device settings and sign-ins remain on that PC. Missing clips hydrate and
verify before Medal starts. Quit Medal normally and wait for **Ready** before
switching. Finish verifies media, uploads and reads back the new generation,
then conditionally releases ownership. An outage or uncertain acknowledgement
keeps ownership and local media for recovery on the same PC. Drive must be
reachable to acquire ownership, even when all clips are already local.

Cloud generations live under `.medal-sync/LIBRARY_ID/generations/` in the
dedicated Drive folder. They contain clip titles, references and normalized
library tables; keep that folder private. Full Medal databases, login/device
settings and provider credentials are not uploaded. Optional cloud replicas
still contain media only. History, ordinary launch/finish, explicit abandonment
and optional-copy retry work in cloud mode. Maintenance that needs an SMB
writer lock (retention, replica pruning and persistent-media refresh) refuses
cloud mode. Keep the previous primary until migration is independently verified;
cloud mode does not automatically migrate an existing SMB journal.

The [cloud example](../tools/medal-sync/config/medal-sync.cloud.example.json)
shows the JSON properties. All participating PCs need a version that supports
cloud mode; older versions reject it.

### Switch primaries

Primaries are interchangeable. Startup downloads only the clips that the
primary holds and this PC lacks. Every finish uploads all referenced clips to
the configured primary and verifies them. To move a library:

1. On the PC with the complete local copy, adopt a configuration whose
   `remote` names the new primary, with the same library ID.
2. While Ready, run `medal-sync storage init-primary`, then repeat with
   `--apply`. It writes the library marker only to an empty folder, or verifies
   an existing marker.
3. Open Medal and finish normally. The next finish uploads and verifies every
   clip at the new primary before releasing the library.
4. Update every other PC's configuration to the new primary.

A PC that still points at the old primary refuses to start if the library
needs clips that are missing there and locally. Nothing is lost. The old
primary is never modified or deleted; remove it yourself once every PC uses the new one.

Enrollment copies, spike promotion and persistent media refresh (`refresh-media`)
work on folder primaries only. Cloud mode supports first-PC relocation into its
persistent local copy before initialization; other rclone primaries refuse relocation.

## Add OneDrive, Dropbox or Google Drive

Authorize a named remote using rclone's interactive configuration. Use an
explicit credential file outside Git, the media folders and the cache:

```powershell
# The rclone bundled with an all-users installation; see below for per-user setup.
& "$env:ProgramFiles/Medal Sync/bin/rclone.exe" config --config "$HOME/.medal-sync/rclone.conf"
```

Choose the provider and name the remote `onedrive`, `dropbox`, or `gdrive`.
Follow the provider's sign-in flow. Instructions and provider restrictions:
[OneDrive](https://rclone.org/onedrive/), [Dropbox](https://rclone.org/dropbox/),
[Google Drive](https://rclone.org/drive/). Protect this file: it holds tokens.
Unattended runs require a config rclone can open without an interactive prompt.

For the native installation, rclone is bundled beside the companion. Use
`$env:ProgramFiles/Medal Sync/bin/rclone.exe` for an all-users installation,
or `bin/rclone.exe` under your chosen per-user program folder.

When creating configuration, add:

```powershell
--cloud-remote onedrive:MedalSync --rclone-config "$HOME/.medal-sync/rclone.conf"
```

The command accepts one initial cloud target. Multiple providers can be added
in the candidate JSON's optional `replication` object:

```json
{
  "replication": {
    "localCopyRoot": "E:\\MedalLibrary",
    "rcloneConfigPath": "C:\\Users\\YOUR-USER\\.medal-sync\\rclone.conf",
    "targets": [
      { "name": "onedrive", "remote": "onedrive:MedalSync" },
      { "name": "dropbox", "remote": "dropbox:MedalSync" },
      { "name": "google", "remote": "gdrive:MedalSync" }
    ]
  }
}
```

Add `"required": true` to a target that must be verified before every
handoff. Other targets are best-effort; see
[offline storage](#offline-storage).

Set `localCopyRoot` to `null` to keep server-with-cache recording and add only
cloud replicas. Use an empty `targets` array for local-and-server storage
without cloud. The companion appends the library ID to each cloud destination
to separate collections. Remotes must name a dedicated folder, not an entire
provider root. Existing configuration files without `replication` retain their
original storage behavior.

## Local storage and cloud without a server

Use `--storage-mode local` and a dedicated persistent folder as `--remote`:

```powershell
& $cli config create --storage-mode local --account-id YOUR_ACCOUNT_ID `
  --new-library --remote E:\MedalLibrary --cache-root E:\MedalCache `
  --coordination-root "$HOME/.medal-sync/coordination" `
  --cloud-remote onedrive:MedalSync --rclone-config "$HOME/.medal-sync/rclone.conf" `
  --out "$HOME/medal-sync-candidate.json"
```

The coordination root defaults to the resolved state directory's `coordination`
folder when omitted in local mode. Preview, validation and guarded initialization
rules still apply. Keep media, cache and journal in separate folders. Omit
`--local-copy-root` because `--remote` is already persistent local storage.
Omit cloud flags to use local storage alone. The JSON property is
`"storageMode": "LOCAL"`; configurations without it retain server mode.

Local mode supervises one PC. Do not synchronize its journal between computers
or reuse its library ID for independent writers. Use server mode with the shared
SMB journal or Google Drive cloud mode for cross-PC handoff.

## Verification, conflicts and recovery

Copies use an exact list of known media references, including thumbnails and
available keyboard overlays. Full Medal databases, native database backups,
logins, arbitrary metadata paths and the live coordination journal are not
uploaded by media replication. Optional cloud copies contain media only; use
the existing snapshot workflow to retain library records separately. Google
Drive cloud mode separately publishes normalized library generations and its
ownership metadata, as described above.

Replication uses [rclone copy](https://rclone.org/commands/rclone_copy/) with
immutable destinations, followed by
[rclone check --download](https://rclone.org/commands/rclone_check/).
Verification reads the selected files back from the destination; plan for
provider bandwidth, API limits, time and possible download charges. Set
`replication.bandwidthLimitMiB` to limit transfer bandwidth (1 to 10000 MiB/s),
or leave it `null` for unlimited bandwidth.
By default every finish verifies all referenced media, including unchanged
clips. Each transfer command has a four-hour default timeout; set
`replication.timeoutMinutes` from 1 to 1440 for your collection and connection.
Interrupted commands are retried on the next finish; completed immutable files
are reused. Verification starts again to prove the current destination state.

Primary storage and targets marked `"required": true` must verify before
handoff. A network error, full quota, failed verification, conflicting
destination file or expired shutdown budget there leaves ownership and local
media on this PC. The supervisor tries again on its own, waiting longer after
each failed attempt: from 30 seconds up to 5 minutes while storage cannot be
reached, and up to 10 minutes after any other failure, because each attempt
copies and verifies again from scratch. A failure that waiting cannot fix shows
as *Action needed* (status *Recovery needed*) with its cause and next step:

- **Sign-in rejected** (an expired or revoked token): run
  `rclone config reconnect "REMOTE:" --config "CREDENTIAL FILE"` on this PC.
- **Credential file missing** at the configured `rcloneConfigPath`, or a
  configuration rclone cannot open (for example one that asks for a password,
  or does not define the remote).
- **Conflicting destination file** with the default `"STOP"` policy.
- **Out of space or quota** at the destination.
- **Verification found missing or different files** at the destination.

Fix the cause and the next automatic attempt finishes the handoff. After a
restart, open Medal through Medal Sync again to resume the finish. rclone's
error text is never stored or shown, since it can contain credentials or private
file names; only the kind of problem is kept. The last verification receipt is
`replication/last-verified.json` under the configured state root; it lists
verified and deferred targets.

The clip drive also reports uploads that failed or were canceled and are waiting
for rclone to retry. Check that primary storage accepts writes and has free
space. When the same queue entry keeps failing for 15 minutes after Medal exits,
finish shows *Action needed* and retries with backoff. SSD cache cleanup trouble
is reported separately and keeps supervision running while the cache cleaner
retries. Ownership and cached clips remain on this PC throughout.

### Incremental verification

Always-full byte verification remains the default. To reduce read-back for a
large library, set `replication.fullVerifyEveryHandoffs` above 1 (at most 100).
Between full passes, new or changed files are copied and byte-verified; unchanged
files get a presence and size check using [rclone check --size-only](https://rclone.org/commands/rclone_check/).
The full pass repeats every N successful replication passes per destination
(including retries), or after `fullVerifyEveryDays` days (default 7, at most 30),
whichever comes first. Set the handoff count back to 1 to always read all bytes.

This is a deliberate tradeoff: same-size corruption of an unchanged destination
file can remain undetected until the next full pass. File identity uses its size
and source modification time; do not edit media while preserving both values.
Required destinations still block handoff unless their selected byte checks and
all remaining presence/size checks pass. Progress and finish results show the
planned files and bytes to read back, summed across destinations.

Private receipts under `replication/destinations/` bind the snapshot digest,
library, source, destination, configuration, credential-file fingerprint and
per-file identities. Missing, damaged, foreign, expired or failed evidence forces
a full pass. Each attempt invalidates its previous receipt before checking files;
an interruption or conflict cannot leave a partially updated receipt reusable.
Keep these receipts private: they name media. Their absence is safe.

### Retry cloud copies while Ready

With Medal quit and the library Ready, preview pending optional cloud copies:

```powershell
medal-sync replicate retry
medal-sync replicate retry --target offsite --apply
```

Omit `--target` to retry every pending optional target. A required target still
verifies during finish and cannot be selected here. The source is this PC's
persistent copy when configured, otherwise primary storage. The run uses the
committed snapshot and rechecks its generation and journal revision throughout.
It never takes ownership or holds the journal writer lock, so another PC may
start normally. A local launch cancels the retry before using media.

Each target leaves `pending.json` only after its copy, verification and final
generation check pass. Outages keep it pending and update its last attempt.
Launch, Ctrl+C, Windows shutdown, configuration changes or another PC starting
cancel unfinished work and invalidate its receipts. Retry after the library is
Ready again. Configured bandwidth and per-command timeout limits still apply.
`status`, `status --json` and the tray show progress and the last result without
changing Ready ownership; stale progress is explicitly reported as stale.

### Check cloud copies on demand

A handoff verifies new and changed files. To read back the whole committed
generation at a cloud copy at any time, preview and then run a check while the
library is Ready and Medal is quit:

```powershell
medal-sync replica verify
medal-sync replica verify --target offsite --apply
medal-sync replica history [--target offsite] [--json]
```

The preview lists the committed generation, the number of referenced files and
bytes, each target and the configured bandwidth and timeout limits. `--apply`
compares every referenced file byte for byte from this PC's persistent copy,
otherwise primary storage. It only reads: it never copies, replaces or deletes
files and never takes ownership, so another PC can still start. A launch on this
PC, Ctrl+C, Windows shutdown, a configuration change or another PC starting
stops the check.

Each attempt is recorded per target in `replication/audit-history.json` under
the state root, with counts only: matched, missing, different and not reached. The last 20
attempts are kept. A result is **passed**, **failed** (missing or different
files) or **incomplete** (offline, sign-in, configuration, canceled, library
changed or error). An incomplete attempt never replaces the last complete
result. A failed target loses its incremental verification receipt, so the next
handoff reads every file back there, and an optional target is queued for
`replicate retry`. A check never counts as the verification a handoff needs.
Exit codes: 0 passed, 1 missing or different files, 3 incomplete or canceled.

### Clean up a cloud copy

Files the library no longer references stay at a cloud copy, as does version
history when `replication.conflictPolicy` is `version`. Cleanup is a reviewed,
two-step operation for optional targets only:

```powershell
medal-sync replica prune --target offsite --out C:\Private\offsite-cleanup.json
medal-sync replica prune --in C:\Private\offsite-cleanup.json --apply
medal-sync replica purge-trash --target offsite [--older-than 7] [--apply]
```

The review lists the destination and plans clip media and sidecars that the
committed generation does not reference. It never plans a referenced file, a
file changed in the last 14 days (`--grace-days N`), a file referenced by an
uncommitted generation from that period, the storage marker, database folders
or anything that is not clip media. Version history under
`.medal-sync-history/` is planned only with `--history-older-than DAYS`, and
only whole, dated attempts. The review file names private paths: keep it on
this PC.

Cleanup refuses duplicate or case-conflicting paths, unsafe names, links and
unreadable listing entries before saving a review or changing the destination.
For duplicate names, inspect the copy with `rclone dedupe` before retrying;
Medal Sync does not choose which duplicate to keep.

Apply holds the journal writer lock, so no PC can start or finish meanwhile. It
refuses if the committed generation changed or any file at the destination
differs from the review, then moves exactly the reviewed files into
`.medal-sync-trash/<review ID>/` at the destination. Nothing is deleted. If the
move is interrupted, apply the same review again: files already in its trash
folder count as moved. `purge-trash` lists trash batches and, with `--apply`,
permanently deletes batches whose files were moved at least `--older-than` days
ago (default 7). It holds the same writer lock while it chooses and deletes
batches, so a prune resumed on another PC cannot add files to a batch that is
being purged. Each invocation lists only `.medal-sync-trash/`, once. With
`--apply`, it deletes exactly the batches marked `deleting` from that listing;
batches marked `kept` remain even if they cross the age limit during reporting.
A missing trash folder is empty. Age counts from the move, not the review, so
applying an old review still gets the full wait. Apply writes an empty `.medal-sync-moved-<time>`
marker into the batch for this; batches without one are kept. Required targets
are refused.

### Check sign-in before recording

Provider sign-ins do not last forever. Some providers expire a sign-in that
has not been used for a while, and Google expires it after seven days for an
OAuth client left in *Testing* status. Check every rclone remote without
recording a session:

```powershell
medal-sync storage check
```

For an rclone primary it reads the library marker; for each cloud target it
lists the configured folder one level deep. Each remote is reported as OK,
offline, *sign in again* (with the `rclone config reconnect` command to run),
a configuration problem (missing credential file, or one that asks for a
password or does not define the remote), folder missing, marker missing, or
another library's folder. A missing target folder is fine: the next finish
creates it. The check never writes to a destination, never stores rclone's
error text and never changes ownership, media or configuration.

Launch runs the same check before taking ownership, when the configuration has
any rclone remote. Outages are ignored there: recording does not depend on
the network. A problem with an optional target shows as a notice. For an rclone
primary or a required target, `replication.credentialCheck` decides: `"WARN"`
(the default) starts Medal with a notice, and `"REFUSE"` keeps Medal closed and
the library Ready until it is fixed. The latest result is kept in
`replication/storage-check.json` under the state root. `status` and the tray
panel show remaining problems until the next check, or until a later finish
verifies that destination.

### Offline storage

Remote outages are waits, not errors:

- **Optional cloud replicas.** If a replica cannot be reached or fails, the
  primary's verified copy still lets the library hand off. The target is
  recorded in `replication/pending.json` with its first failure and latest
  attempt. `status`, the tray panel and the
  finish result name it, and the next finish retries it. Any failure is
  deferred, including a conflicting destination file, so check `status` for
  long-pending targets. Medal reopening and Windows shutdown still stop the
  finish as before.
- **Primary storage or the server journal.** Finish keeps ownership and media
  on this PC and reports *Waiting for storage*. The supervisor retries with
  backoff from 30 seconds up to 5 minutes and completes the handoff when
  storage returns.
- **At launch.** With a persistent local copy that already holds every
  referenced clip, Medal starts and records locally while the primary is
  offline. If clips must be downloaded first, or the server journal is
  unreachable, launch waits (exit code 3) and changes nothing. If the primary
  instead rejects sign-in or its credential file is missing, Medal still starts
  when nothing must be downloaded, and a notice in the status panel says the
  handoff cannot finish until that is fixed.

The copier adds files and refuses conflicting replacements. It does not
propagate deletions or remove older replica files. Removing a clip from Medal
removes its library reference; retained replica files do not recreate that
reference. Review conflicts and old files explicitly before deleting anything.
Editing an existing clip in place may require resolving a replica conflict.

For automatic updates with retained prior versions, choose
`--replica-conflicts version` when creating configuration, or set
`replication.conflictPolicy` to `"VERSION"`. Before replacing a destination
file, rclone moves the previous file beneath
`.medal-sync-history/TIMESTAMP-ATTEMPT/` at that destination. The replacement is
then verified byte for byte. Histories are never copied back into the library
or automatically pruned. Budget space for retained versions. The default
`"STOP"` policy refuses replacements. Hydration from the server always refuses
conflicting local files to protect unpublished work.

### Review and refresh persistent local media

When another PC publishes changed bytes at an existing path, close Medal and
finish the local mounted session before reviewing the persistent copy:

```powershell
medal-sync refresh-media --out C:\Private\media-review.json
medal-sync refresh-media --in C:\Private\media-review.json --apply
```

The preview lists each conflicting relative path. A byte-for-byte match to a
file preserved beneath the server's `.medal-sync-history` is identified as
matching retained history. Every other differing local file is an **unknown
local modification**; do not assume it is stale. Inspect those paths and keep
unpublished work. Replacing unknown files requires explicitly adding
`--accept-unknown-media` when applying this exact plan. This approval covers
all unknown conflicts listed in that plan. Preview and apply never start Medal.

The private plan binds the library, device, configuration, committed generation,
ownership revision and hashes/identities of every referenced server/local file.
Keep the plan local: it contains identifiers and media paths. A changed review
state refuses before replacing files; prepare and review a new plan while the
journal is Ready. Compatibility, idle Medal, storage identity and configuration
are checked again immediately before replacement. Ordinary unattended hydration
keeps refusing conflicts, including with the VERSION publication policy.

Apply acquires ownership, saves the plan and copies every replaced local file
to `Backups/media-refresh-PLAN_ID/previous/` under the resolved state root.
Only after verifying each backup does it stage and verify replacement bytes on
the persistent volume and atomically replace the reviewed file. Budget space
for retained backups and a staged media file. Backups are never automatically
pruned. This workflow copies only the committed library's referenced media;
it does not alter the database or resurrect deleted clips.

An interruption retains ownership, the original review and all preserved bytes.
Ordinary launch and finish refuse that refresh session. Retry the same
`refresh-media --in ... --apply` command on the same PC, with the same explicit
unknown-file approval if needed. It verifies already replaced files and backups
before continuing. Partial staged writes are preserved as `partial-*` recovery
files before retrying. A changed source, unexpected local edit or damaged backup
refuses further replacement and retains evidence for manual recovery. Restore
the reviewed configuration/device identity before retrying if either changed.
After the entire reviewed file set verifies again, the journal returns to Ready;
open Medal through Medal Sync normally. An explicit abandon remains an operator
recovery decision and does not reconcile local media.

### Library history

Every switch that changes the library publishes a new numbered generation on
the server. Two read-only commands show that history. They never take
ownership, hold the server lock, restore anything or delete anything, so they
are safe while another PC records:

```powershell
medal-sync history list [--json] [--limit 20] [--before N]
medal-sync history diff --from N --to M [--json] [--out C:\Private\changes.json]
```

`history list` starts at the committed head, newest first, and shows each
generation's parent, time, PC, Medal Sync and Medal versions and row counts.
Use `--before` with the number it prints to see older pages. Generations that
are not in the head's ancestry are marked *uncommitted*: an abandoned or
unfinished switch left them, and they are not part of the shared history. If a
generation in the ancestry is missing or damaged, the list says the history is
incomplete and marks older folders *unknown* instead of guessing.

`history diff` prints counts only: clips added, removed with and without a
deletion record, changed, and which file paths and metadata fields changed. It
says whether `--from` is an ancestor of `--to`. It refuses damaged generations
and generations from different Medal schemas. `--out` writes each clip's ID,
title and paths to a new file; it never replaces an existing file. That file is
private: keep it on this PC.

Generations removed by [retention](#retention) are listed as removed ranges,
not as damage, and `history diff` explains that a removed generation cannot be
compared.

Both commands report the committed head they read. A switch that finishes while
they run appears the next time. They exit 3 when the server cannot be reached.

### Retention

Generations and this PC's recovery folders accumulate. Retention removes old
ones after a review, while the library is Ready:

```powershell
medal-sync retention --out C:\Private\retention.json [--keep 20] [--keep-days 90] [--grace-days 30]
medal-sync retention --in C:\Private\retention.json --apply
```

It always keeps the committed head, its `--keep` newest committed ancestors,
anything from the last `--keep-days` days, the newest committed generation each
PC published and the highest-numbered generation folder. Uncommitted attempts
and interrupted `.staging-*` folders are kept for `--grace-days`. On this PC it
keeps the newest `--keep` accepted-change reviews, media refresh backups and
aborted-start folders plus any from the last `--keep-days` days; aborted-start
evidence is kept while its cache still holds files. Clip media and the library
database are never touched.

Apply refuses unless the library is exactly as reviewed. It holds the journal
writer lock and the single-instance lock, so no PC can start meanwhile. It first
records what it removes in `generations/retention.json`, then renames each
folder into a trash folder and deletes the trash. An interrupted apply leaves
whole folders; apply the same review again. The record keeps the highest number
ever used and the full committed chain, so numbers are never reused, `history
list` shows removed ranges and a PC can still prove its library matches an
older shared generation. A PC whose library matches only a removed generation
needs `medal-sync launch --replace-local-library` (a backup is kept). Update
every PC to 1.3.0 or later first; the review warns about any PC that last
published with an older version.

Consumer desktop sync cannot provide the SMB ownership lock. Google Drive
cloud mode instead uses conditional API metadata updates; other rclone
primaries still require the SMB journal. Automatic two-way conflict merging
is not supported. Local mode avoids the server by keeping coordination on one
PC. The reproducible integration suite
uses real rclone processes, named local test remotes and real WinFsp mounts.
It verifies publication, retry, cancellation, retention, versioned updates and
sequential synthetic PCs without contacting OneDrive, Dropbox or Google Drive.
It also checks Google Drive cloud-mode ownership against a synthetic metadata
service, including real rclone generation/media transfers and a real WinFsp
round trip. Provider-specific account, conditional-update and quota behavior
requires separate live acceptance; see [tests](../tests/README.md).
