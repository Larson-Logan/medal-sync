# Storage choices

Medal Sync supports primary storage on an SMB server or any named rclone remote
(cloud or a personal server), a persistent local media collection, and cloud
replicas. Server mode shares one-PC-at-a-time library ownership through the SMB
journal. Local mode keeps its media and journal on one PC and requires no
server; replicas send its media to cloud storage.

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed virtual drive, uploaded to SMB | Server media; finished session cache is removed |
| Local copy and server | Virtual drive backed by a persistent local folder | Complete referenced media on the local disk and server |
| Local storage, with optional cloud copies | Virtual drive backed by a persistent local folder | Local media and optional verified cloud copies; no SMB dependency |
| Cloud or personal-server primary | Virtual drive backed by a persistent local folder | Local media and the rclone primary; journal stays on the server |
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

The setup wizard and launch refuse these, and `inventory` lists them as blockers:

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

## Keep a local copy and upload to the server

Add `--local-copy-root` to the configuration workflow in
[getting started](getting-started.md):

```powershell
$cli = "$env:ProgramFiles/Medal Sync/bin/medal-sync.exe"
& $cli config create --account-id YOUR_ACCOUNT_ID --new-library `
  --cache-root E:\MedalCache --local-copy-root E:\MedalLibrary `
  --remote '\\YOUR-SERVER\Data\Media\Videos' `
  --coordination-root '\\YOUR-SERVER\Data\AppData\MedalSync' `
  --rclone-path C:\Tools\rclone.exe --out "$HOME/medal-sync-candidate.json"
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

See the [cloud primary example](../tools/medal-sync/config/medal-sync.cloud.example.json).

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

Enrollment copies, relocation copies, spike promotion and persistent media
refresh (`refresh-media`) work on folder primaries only. They refuse an rclone primary.

## Add OneDrive, Dropbox or Google Drive

Authorize a named remote using rclone's interactive configuration. Use an
explicit credential file outside Git, the media folders and the cache:

```powershell
& C:\Tools\rclone.exe config --config "$HOME/.medal-sync/rclone.conf"
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
or reuse its library ID for independent writers. Use server mode and the shared
SMB journal for automatic cross-PC handoff.

## Verification, conflicts and recovery

Copies use an exact list of known media references, including thumbnails and
available keyboard overlays. Full Medal databases, native database backups,
logins, arbitrary metadata paths and the live coordination journal are not
uploaded. Cloud copies contain media only; use the existing snapshot workflow
to retain library records separately.

Replication uses [rclone copy](https://rclone.org/commands/rclone_copy/) with
immutable destinations, followed by
[rclone check --download](https://rclone.org/commands/rclone_check/).
Verification reads the selected files back from the destination; plan for
provider bandwidth, API limits, time and possible download charges. Set
`replication.bandwidthLimitMiB` to limit transfer bandwidth (1 to 10000 MiB/s),
or leave it `null` for unlimited bandwidth.
Every finish currently verifies all referenced media, including unchanged
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

Both commands report the committed head they read. A switch that finishes while
they run appears the next time. They exit 3 when the server cannot be reached.

Cloud folders cannot replace the SMB ownership journal: consumer sync does
not provide the locking used by this companion. An rclone primary holds media
only. Cloud-only coordination and
automatic two-way conflict merging are not supported. Local mode avoids the
server by keeping coordination on one PC. The reproducible integration suite
uses real rclone processes, named local test remotes and real WinFsp mounts.
It verifies publication, retry, cancellation, retention, versioned updates and
sequential synthetic PCs without contacting OneDrive, Dropbox or Google Drive.
Provider-specific account and quota behavior requires separate service
acceptance when you choose to connect those accounts.
