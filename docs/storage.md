# Storage choices

Medal Sync supports an SMB server, a persistent local media collection, and
cloud or personal-server primary storage, and optional or required replicas.
Server mode shares one-PC-at-a-time library ownership
through SMB. Local mode keeps its media and journal on one PC and requires no
server; optional replicas send its media to cloud storage.

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed virtual drive, uploaded to SMB | Server media; finished session cache is removed |
| Local copy and server | Virtual drive backed by a persistent local folder | Complete referenced media on the local disk and server |
| Local storage, with optional cloud copies | Virtual drive backed by a persistent local folder | Local media and optional verified cloud copies; no SMB dependency |
| Cloud/personal-server primary with SMB journal | Virtual drive backed by a persistent local folder | Verified primary media and retained local collection |
| Either mode with cloud replicas | Same recording path as above | Primary verified; optional unavailable copies deferred |

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

Before Medal starts, the companion downloads and verifies only referenced
media missing from the local collection. Existing local files remain untouched.
Each required file must exist locally or on the primary.
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
      { "name": "google", "remote": "gdrive:MedalSync", "required": true }
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

## Use a cloud provider or personal server as primary

Server-mode `remote` can name a dedicated rclone folder, such as
`onedrive:MedalSync`, `gdrive:Medal`, `sftp-home:Media/Videos`, or
`r2:medal-sync`. Set both `replication.localCopyRoot` and an explicit local
`replication.rcloneConfigPath`. The virtual drive records to the persistent
local collection; `coordinationRoot` remains on SMB for atomic ownership.
See the [cloud primary example](../tools/medal-sync/config/medal-sync.cloud.example.json).
The capture subfolder is appended to the remote: `sftp-home:Media/Clips` for
`M:\Clips`. Replicas still append the library ID to their own destinations,
which must not overlap the primary folder. Credentials stay outside media,
cache, coordination and exported configuration.

To switch primaries, finish the current session first and retain the existing
configuration. Keep the same library ID, SMB journal and persistent local
collection; update only the primary destination and necessary credentials.
Run `medal-sync storage init-primary` to preview, then add `--apply` to mark
an empty destination or verify an existing matching marker. An unmarked
nonempty destination or another library's marker is refused. No old primary
is modified or deleted. The next finish copies all referenced local media
to the new primary and verifies it byte for byte before releasing ownership.
For a fresh cloud primary, initialize its marker before `init --apply`; all
initial library media must already be present there before creating generation 0.

SMB-specific enrollment, relocation, and spike promotion refuse an rclone
primary. Use those workflows with an SMB primary before switching.

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

Primary storage always requires verification. Replica targets are best-effort
by default; set `"required": true` on a target to make its failure block handoff.
Unavailable optional copies are recorded by name with their first failure and
last attempt in `replication/pending.json`. The next finish retries them and
clears successful targets. `replication/last-verified.json` separately records
verified and deferred targets for the last completed replication attempt.

A primary outage leaves ownership and local media on this PC and shows
**Waiting for storage**. The running supervisor retries after 30 seconds, then
60, 120, 240, and at most 300 seconds. Handoff finishes after storage returns
and verification passes. An unavailable SMB journal at launch returns Waiting
(exit 3); reopen Medal once it returns. A configured primary may be offline at
launch if all required media is already local, but the journal must be reachable.
Missing local media waits for storage without opening Medal. A failed required
copy, conflicting file, unsafe marker, shutdown budget, or reopened Medal still
retains the session. Cancellation never counts as an optional provider failure.

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
`"STOP"` policy refuses replacements. Hydration never replaces existing local files. Possible unpublished local work
stays on this PC; differing primary files are refused at publication unless
the configured version policy explicitly preserves their previous bytes.

Cloud folders cannot replace the SMB ownership journal: consumer sync does
not provide the locking used by this companion. Cloud-only coordination and
automatic two-way conflict merging are not supported. Local mode avoids the
server by keeping coordination on one PC. The reproducible integration suite
uses real rclone processes, named local test remotes and real WinFsp mounts.
It verifies publication, retry, cancellation, retention, versioned updates and
sequential synthetic PCs without contacting OneDrive, Dropbox or Google Drive.
Provider-specific account and quota behavior requires separate service
acceptance when you choose to connect those accounts.
