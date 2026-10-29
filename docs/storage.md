# Storage choices

Medal Sync supports an SMB server, a persistent local media collection, and
required cloud replicas. Server mode shares one-PC-at-a-time library ownership
through SMB. Local mode keeps its media and journal on one PC and requires no
server; optional replicas send its media to cloud storage.

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed virtual drive, uploaded to SMB | Server media; finished session cache is removed |
| Local copy and server | Virtual drive backed by a persistent local folder | Complete referenced media on the local disk and server |
| Local storage, with optional cloud copies | Virtual drive backed by a persistent local folder | Local media and optional verified cloud copies; no SMB dependency |
| Either mode with cloud replicas | Same recording path as above | Media copied and verified at every configured cloud destination |

The local collection and VFS cache are separate. `localCopyRoot` names a
dedicated storage root, for example `E:\MedalLibrary`; with capture folder
`M:\Clips`, persistent files live in `E:\MedalLibrary\Clips`. Never place
either folder in a provider's desktop sync folder. Provider access uses rclone
directly. Allow disk space for the collection, cache and new recordings.

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
generation's referenced media from the server into the local collection.
The virtual drive then writes to that persistent collection. After Medal
quits and VFS writes drain, the companion copies current referenced media
to SMB, verifies it byte for byte, and checks server assets before releasing
ownership. Completed-session cleanup removes only the VFS cache; the local
collection stays. Initial hydration can take time for a large library.

The SMB journal must be reachable to acquire ownership. This is not an
offline multi-writer mode. A server outage during recording leaves local
work in place; recovery and handoff wait for the server to return.

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

All configured targets are required. A network error, full quota, failed
verification, conflicting destination file or expired shutdown budget leaves
ownership and local media on this PC. Quit Medal normally and retry `finish`
when the cause is resolved. The last successful verification receipt is
`replication/last-verified.json` under the configured state root.

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

Cloud folders cannot replace the SMB ownership journal: consumer sync does
not provide the locking used by this companion. Cloud-only coordination and
automatic two-way conflict merging are not supported. Local mode avoids the
server by keeping coordination on one PC. The reproducible integration suite
uses real rclone processes, named local test remotes and real WinFsp mounts.
It verifies publication, retry, cancellation, retention, versioned updates and
sequential synthetic PCs without contacting OneDrive, Dropbox or Google Drive.
Provider-specific account and quota behavior requires separate service
acceptance when you choose to connect those accounts.
