# Configuration reference

Each PC keeps its settings in `config.json` in its Medal Sync user data folder
(see [where settings are read from](commands.md#where-settings-are-read-from)).
Create it with the configuration wizard or `config create`, as described in
[getting started](getting-started.md#plan-configuration-before-using-live-data),
rather than by hand. This page lists every field so you can review a file or
add settings the wizard does not offer, such as more cloud copies.

`config.json` holds no passwords or tokens. SMB uses the PC's saved Windows
login, and cloud sign-in stays in the separate rclone credential file.

## Configuration wizard, field by field

The wizard shows only the fields your storage mode uses. Each field's
**Learn more** link opens its section below. Create the folders you name
before choosing **Review**; the wizard checks them but does not create them.

A typical first setup with a server, on a PC whose SSD is `E:`:

| Field | Example |
| --- | --- |
| Storage mode | Server (SMB) |
| Library choice | Create a new library ID |
| Medal account ID | Filled in for you |
| Storage share or remote | `\\SERVER\Data\Media\Videos` |
| Coordination share | `\\SERVER\Data\AppData\MedalSync` |
| Capture folder / drive | `M:\Clips` (keep the default) |
| SSD cache folder | `E:\MedalCache` |
| Everything else | Keep the default, or leave empty |

### Storage mode

Where your clips live, and how PCs take turns with the library. Not sure?
Answer [which mode should I pick?](getting-started.md#which-mode-should-i-pick)

- **Server (SMB):** clips go to a NAS or home server share. Pick this if you
  record on two or more PCs and have a server. You can also enter an rclone
  remote here; see [use a cloud provider as primary storage](storage.md#use-a-cloud-provider-or-personal-server-as-primary-storage).
- **Local storage:** clips stay on this PC. Pick this for one PC with no server.
- **Cloud (no server):** Google Drive, OneDrive or Dropbox holds the clips and
  decides which PC owns the library. Pick this for several PCs without a
  server. See [cloud mode without a server](storage.md#cloud-mode-without-a-server).

### Library choice and library ID

On the **first** PC, choose **Create a new library ID**. The wizard makes one
when you choose **Review**.

On **every other** PC, choose **Join an existing library ID** and paste the ID
from the first PC. To find it there, open the `config.json` shown at the top of
the wizard on that PC (usually `%USERPROFILE%\.medal-sync\config.json`) and
copy the `libraryId` value: 32 letters and digits. Never create a second library for clips another PC already shares.

### Medal account ID

The number Medal uses for your account. Sign in to Medal on this PC first; the
wizard then fills it in. If it is empty, sign in to Medal and choose **Detect**,
or run `medal-sync inspect` and copy the account ID it prints. Every PC sharing
a library must use the same Medal account.

### PC display name

Optional. A friendly name, such as `Desk PC` or `Living room`, that other PCs
show in status and history. Leave it empty to show the computer and user name
instead.

### Storage folder

Where the complete clip collection is kept. Its label depends on the mode:

- **Server:** a UNC path to a folder on your server, such as
  `\\SERVER\Data\Media\Videos`, that every PC can open with its saved Windows
  login. Use the `\\SERVER\...` form rather than a mapped drive letter.
- **Local:** an empty, dedicated folder on a disk with room for every clip,
  such as `D:\MedalLibrary`.
- **Cloud:** the rclone remote name you signed in with, a colon, and a folder,
  such as `gdrive:MedalSync`. Sign in under **Cloud sign-in** first; the remote
  name you chose there goes before the colon.

### Coordination folder

A small folder that records which PC owns the library and the history of
library changes. It never holds clips.

- **Server:** a separate folder on the same server, such as
  `\\SERVER\Data\AppData\MedalSync`. Every PC must use the same one.
- **Local** and **Cloud:** filled in for you with a folder in Medal Sync's own
  settings folder. Keep the default.

Keep it apart from the storage, cache and capture folders.

### Cloud journal ID

Cloud mode only. It identifies the file in your cloud folder that decides which
PC owns the library.

On the **first** PC, fill in the cloud storage folder and sign in, then choose
**Get ID**. On **every other** PC, paste the same ID from the first PC's
`config.json` (`cloudJournalId`). See [cloud mode without a server](storage.md#cloud-mode-without-a-server).

### Capture folder and drive

The folder Medal records into. Keep the default, `M:\Clips`, unless another
program already uses `M:`. Medal Sync creates this drive each time it starts
Medal, so it does not exist yet; that is why there is no **Browse** button. Use
a drive letter from D to Z and a folder, not just `M:\`.

### SSD cache folder

An empty folder on this PC's fastest local disk, such as `E:\MedalCache`. Medal
records here first, then the clips upload. It must not be a network drive, the
storage folder or the capture drive. See
[choose drives and file systems](storage.md#choose-drives-and-file-systems).

### Rclone executable

The program Medal Sync uses to create the clip drive and copy clips. The copy
installed with Medal Sync is selected for you; keep it unless you were told
otherwise.

### Persistent local copy

A folder on this PC that keeps every clip, so you can play any clip without
reaching the server.

- **Server mode:** optional. Leave it empty to keep only recent clips in the
  cache. Use it on a slow connection, or when the storage folder is an rclone
  remote (then it is required). It needs room for your whole collection. See
  [keep a local copy](storage.md#keep-a-local-copy-and-upload-to-the-server).
- **Cloud mode:** required. Medal records into this folder, and Medal Sync
  uploads it.

### Provider credential file and cloud sign-in

The credential file is where rclone keeps your Google Drive, OneDrive or
Dropbox sign-in. You don't need to create it: leave **Provider credential
file** empty, then use **Cloud sign-in**.

1. Choose the provider.
2. Type a short remote name, such as `gdrive`, `onedrive` or `dropbox`. You
   will reuse this name in the storage folder or cloud targets
   (`gdrive:MedalSync`).
3. Choose **Sign in** and approve access in the browser page that opens.

When the check passes, the wizard fills in the credential file. Medal Sync
never sees your password or token. For Google Drive, consider
[your own OAuth client](https://rclone.org/drive/#making-your-own-client-id);
publish it, because a client left in *Testing* status expires every sign-in
after seven days. Leave this section alone in server or local
mode unless you add cloud copies. To sign in from a terminal instead, see
[add OneDrive, Dropbox or Google Drive](storage.md#add-onedrive-dropbox-or-google-drive).

### Cloud targets

Optional extra copies of your clips in the cloud, one per line, in the form
`name=remote:folder`:

```text
optional onedrive=onedrive:MedalSync
required gdrive=gdrive:MedalBackup
```

`name` is your label, shown in status. `remote` is a name you signed in with
under **Cloud sign-in**. Start a line with `required ` if another PC must wait
until that copy is verified, or `optional ` to let it catch up later. Leave the
box empty for no cloud copies. See [targets](#replication-local-copy-and-cloud-copies).

### Syncthing exclusion

Leave both fields empty unless you use Syncthing and one of its shared folders
contains your clip folder. Then enter that Syncthing folder's ID and the clip
folder's path inside it, such as `/Videos/Clips`, so Syncthing ignores the
clips Medal Sync already handles.

### Cache limit, free space and session hours

The defaults suit most PCs.

- **Cache limit:** the most GiB of uploaded clips the SSD cache keeps for quick
  replay. New recordings stay until they upload, even above this.
- **Minimum free space:** GiB Medal Sync always leaves free on the cache's disk.
- **Maximum session hours:** your longest recording session. Medal Sync starts
  Medal only when this much recording fits on the cache's disk.

Lower the numbers if Medal Sync refuses to launch for lack of space on a small
SSD. See [low disk space while recording](storage.md#low-disk-space-while-recording).

## Before editing

- Wait for **Ready**, and keep a copy of the current file.
- Edit a copy, then check it with `medal-sync config validate --config FILE`.
  Validation names every problem it finds and exits 1. It checks syntax and
  paths only; it does not contact storage.
- Unknown fields are refused, so a misspelled field name fails validation
  instead of being ignored.
- Keep `libraryId`, `accountId` and the storage locations unchanged unless you
  are deliberately moving the library; see
  [switch primaries](storage.md#switch-primaries).
- Never copy `device.json` from another PC.

Paths in JSON need doubled backslashes, for example `"E:\\MedalCache"`.
`rclonePath` and `replication.rcloneConfigPath` may use environment variables
such as `%USERPROFILE%`.

Ready-made examples, with placeholder values:
[server](../tools/medal-sync/config/medal-sync.example.json),
[local](../tools/medal-sync/config/medal-sync.local.example.json) and
[Cloud mode using Google Drive](../tools/medal-sync/config/medal-sync.cloud.example.json).

## Top-level fields

| Field | Default from `config create` | Meaning |
| --- | --- | --- |
| `format` | `1` | File format. Must be `1`; see [versions and schemas](versioning.md#file-formats). |
| `storageMode` | `"SERVER"` | `"SERVER"` shares the library through an SMB journal. `"LOCAL"` keeps media and journal on this PC. `"CLOUD"` uses Google Drive, OneDrive or Dropbox for media, generations and conditional ownership updates. |
| `cloudJournalId` | Absent | Required in cloud mode. Get it with `storage cloud-id` and reuse it on every PC sharing the library. See [cloud setup](storage.md#cloud-mode-without-a-server). |
| `libraryId` | Generated for a new library | 32 lowercase hexadecimal characters. Every PC sharing a library uses the same ID. |
| `accountId` | From `--account-id` | Numeric Medal account ID, as reported by `inspect`. Must match the account signed in to Medal. |
| `coordinationRoot` | From `--coordination-root`; state directory's `coordination` folder in local/cloud modes | Server: UNC journal/generation share. Local: dedicated local journal folder. Cloud: dedicated local immutable-history cache; ownership is read from Drive. |
| `remote` | From `--remote` | Primary storage. Server mode accepts a UNC share or named rclone remote and folder. Local mode uses a dedicated local folder. Cloud mode uses a direct Google Drive, OneDrive or Dropbox OAuth remote and folder and requires `replication.localCopyRoot` and `replication.rcloneConfigPath`. |
| `captureFolder` | `"M:\\Clips"` | Folder Medal records into, on the clip drive Medal Sync mounts. The drive letter must be D to Z; it cannot be a drive root. |
| `cacheRoot` | From `--cache-root` | Local SSD folder for the clip drive's cache. Not a network drive or the clip drive. |
| `rclonePath` | The bundled `rclone.exe` | rclone executable used for the clip drive and copies. |
| `mount` | See below | Clip drive settings. |
| `maxSessionHours` | `4` | Longest session to plan for. Launch checks that this much recording fits on the cache's disk, above the free-space reserve. More than 0, at most 24. |
| `syncExclusions` | `[]` | Syncthing folders that overlap the clip folder and must ignore it. Each entry is `{ "folderId": "ID", "pattern": "/Videos/Clips" }`, with one rooted folder pattern and no wildcards. `inspect` reports whether each ignore rule is in place. Most setups leave this empty. |
| `replication` | Absent | Optional persistent local copy and cloud copies. See below. |
| `betaUpdates` | Absent (`false`) | `true` lets this PC's update checks offer [betas](versioning.md#betas) built from `dev` as well as releases, whichever is newer. Betas are less tested than releases. Set it back to `false`, or remove it, to return to releases only: the next release at or above your beta is offered as usual. Earlier Medal Sync versions refuse a file with this field. |
| `keepAwakeWhileFinishing` | Absent (`true`) | While this PC holds work that blocks a handoff (uploads and verification after Medal exits, an idle sync pass, or `replicate retry --apply`), Medal Sync asks Windows not to sleep on its idle timer. Only the system stays awake, not the display, and manual sleep, shutdown, lid and battery policies still apply. `powercfg /requests` lists the request under `SYSTEM`. Set `false` to let the PC sleep on its normal power plan. Omitted unless set; earlier Medal Sync versions refuse a file with this field. |
| `deviceLabel` | Absent | Optional PC display name, such as `Desk PC` or `Living room`: 1–64 printable characters on one line. Set it in the wizard or with `config create --device-label NAME`. Other PCs see it in new ownership, request and history records; earlier records keep their original names. It never changes the device ID or the computer/user check in `device.json`. Omitted unless set, so older versions can still load configurations without it; remove this field before downgrading. |

## `mount`: the clip drive

These become rclone mount options; `medal-sync mount-plan` prints the result.
The defaults suit most PCs.

| Field | Default | Limits | Meaning |
| --- | --- | --- | --- |
| `writeBackSeconds` | `5` | Whole seconds, above 0 | How long a finished file waits in the cache before upload starts (`--vfs-write-back`). |
| `cacheMaxGiB` | `20` | At least 1 | Soft size limit for cached files (`--vfs-cache-max-size`). New recordings stay cached until they upload, even past this limit. |
| `minFreeGiB` | `20` | At least 1 | Free space to keep on the cache's disk (`--vfs-cache-min-free-space`). Launch and downloads into the persistent copy refuse to go below it. |
| `spaceWarningGiB` | Twice `minFreeGiB`, and at least 10 above it | Above `minFreeGiB`, at most 1000000 | Free space at which the *Low disk space* notice appears while recording. See [low disk space while recording](storage.md#low-disk-space-while-recording). |
| `cacheMaxAgeSeconds` | `3600` | Whole seconds, above 0 | How long unused files stay in the cache (`--vfs-cache-max-age`), so a clip played again soon after is read from the SSD. The cache never grows past `cacheMaxGiB`. |
| `cachePollSeconds` | `30` | Whole seconds, above 0 | How often rclone checks the cache for expired files (`--vfs-cache-poll-interval`). |
| `dirCacheHours` | `1000` | At least 1 | How long folder listings are cached (`--dir-cache-time`). |
| `transfers` | `2` | 1 to 8 | Uploads run in parallel (`--transfers`). |
| `controlPort` | `53684` | 1024 to 65535 | Local port for rclone's control API, on 127.0.0.1 only. `config create --spike` uses `53683`. Avoid `53682` (browser sign-in) and `5572` (rclone's default control API). |
| `bandwidthLimitMiB` | `null` (2 MiB/s) | 1 to 10000 | Clip-drive transfer limit in MiB/s while Medal runs. See [limit upload speed while recording](storage.md#limit-upload-speed-while-recording). |
| `finishBandwidthLimitMiB` | `null` (unlimited) | 1 to 10000 | Clip-drive transfer limit in MiB/s after Medal exits, while uploads finish. |
| `recordingBufferHoldSeconds` | `null` (off) | 60 to 3600, above `writeBackSeconds` | Server mode without a persistent local copy only: while a game records, Medal's replay-buffer segments wait this long on this PC instead of uploading, and every other file still uploads after `writeBackSeconds`. See [keep replay-buffer traffic off the server](storage.md#keep-replay-buffer-traffic-off-the-server). Omitted unless set; earlier versions refuse a file with this field. |

Older setups may use `53682`. rclone's [browser sign-in server](https://github.com/rclone/rclone/blob/v1.75.2/lib/oauthutil/oauthutil.go)
needs that port for `rclone config` and `rclone config reconnect`, so a clip drive
using it prevents sign-in even when a handoff is waiting for renewed credentials.
`config validate` warns about this setting without changing it.

To change it, quit Medal normally, wait for **Ready** and the clip drive to stop,
back up `config.json`, then set `mount.controlPort` to `53684` and validate again.
Keep the original port during any unfinished session, including after a reboot:
the running or recovered drive must still be reached on that port. If sign-in is
already blocking a handoff, use rclone's [remote authorization flow](https://rclone.org/remote_setup/)
from another PC to finish sign-in first, then let the handoff reach Ready before
changing the port. Saving other fields in the wizard preserves an existing port.

## `replication`: local copy and cloud copies

Leave `replication` out, or set it to `null`, for server mode with only the SSD
cache. See the [storage guide](storage.md) for how each choice behaves.

| Field | Default | Limits | Meaning |
| --- | --- | --- | --- |
| `localCopyRoot` | `null` | A dedicated local folder | Keeps a complete local copy of the collection, which the clip drive records into. Required for an rclone primary. Leave `null` in local mode, where `remote` is already local. |
| `rcloneConfigPath` | `null` | A local file outside media, cache and coordination folders | rclone credential file. Required for cloud copies and for an rclone primary. |
| `targets` | `[]` | At most 8 | Cloud copies; see below. |
| `conflictPolicy` | `"STOP"` | `"STOP"` or `"VERSION"` | What to do when a destination already has a different file. `"STOP"` refuses; `"VERSION"` moves the old file into `.medal-sync-history/` first. See [verification, conflicts and recovery](storage.md#verification-conflicts-and-recovery). |
| `timeoutMinutes` | `240` | 1 to 1440 | Time limit for each copy or verification command. |
| `fullVerifyEveryHandoffs` | `1` | 1 to 100 | Verify all media bytes every N replication passes per destination. A due full check may run during idle; finish reuses that evidence once. Idle repeats do not consume the interval. Larger values opt into incremental checks between full passes. Retries outside idle also count as passes. |
| `fullVerifyEveryDays` | `7` | 1 to 30 | Maximum age in days of a full verification when incremental checks are enabled. Missing, invalid or failed receipts always force a full pass sooner. |
| `bandwidthLimitMiB` | `null` (unlimited at finish) | 1 to 10000 | Transfer limit in MiB/s for copies to rclone remotes at finish. Idle replication to any destination, including SMB, uses at most 2 MiB/s or this limit if lower. |
| `prefetchWhileIdle` | Absent (on) | `true` or `false` | With `localCopyRoot`, an idle PC that does not own the library downloads clips another PC published into its local copy, so its next launch starts sooner. `false` turns it off. See [download new clips in the background](storage.md#download-new-clips-in-the-background). Omitted unless set; earlier versions refuse a file with this field. |
| `credentialCheck` | `"WARN"` | `"WARN"` or `"REFUSE"` | What launch does when an rclone primary or a required target fails its sign-in check. `"WARN"` starts Medal with a notice; `"REFUSE"` keeps Medal closed and the library Ready. Optional targets and outages only ever produce a notice. See [check sign-in before recording](storage.md#check-sign-in-before-recording). |

Each entry in `targets`:

| Field | Default | Meaning |
| --- | --- | --- |
| `name` | Required | Your label for the copy, shown in status. 1 to 32 letters, digits, `-` or `_`, unique within the file. |
| `remote` | Required | A named rclone remote and a dedicated folder, such as `onedrive:MedalSync`. Not a provider's root, and not overlapping another target or the primary. The library ID is added to the path automatically. |
| `required` | `false` | `true` means this copy must verify before every handoff. Otherwise a failed or offline copy is retried later; see [offline storage](storage.md#offline-storage). |

## Common changes

**Add a second cloud copy.** Sign in with `rclone config` as in
[add OneDrive, Dropbox or Google Drive](storage.md#add-onedrive-dropbox-or-google-drive),
then add an entry to `replication.targets`.

**Limit uploads over a VPN.** Set `mount.bandwidthLimitMiB`, and optionally
`mount.finishBandwidthLimitMiB`.

**Warn earlier about low disk space.** Set `mount.spaceWarningGiB` above
`mount.minFreeGiB`.

**Plan for longer sessions.** Raise `maxSessionHours` (at most 24). Launch then
needs more free space on the cache's disk.

After saving a change, validate the file, then open Medal through Medal Sync as
usual. The new settings apply from the next session.
