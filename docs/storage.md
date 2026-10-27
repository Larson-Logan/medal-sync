# Storage choices

Medal Sync supports an SMB server, a persistent local media collection, and
required cloud replicas. All modes retain the same one-PC-at-a-time library
ownership. An SMB coordination share remains required on every PC.

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed virtual drive, uploaded to SMB | Server media; finished session cache is removed |
| Local copy and server | Virtual drive backed by a persistent local folder | Complete referenced media on the local disk and server |
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
provider bandwidth, API limits, time and possible download charges.
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

Cloud folders cannot replace the SMB ownership journal: consumer sync does
not provide the locking used by this companion. Cloud-only coordination and
automatic two-way conflict merging are not supported. Live cloud sign-in,
quota behavior and two-PC playback require acceptance with your providers;
synthetic tests do not establish those results.
