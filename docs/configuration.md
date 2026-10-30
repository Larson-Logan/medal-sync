# Configuration reference

Each PC keeps its settings in `config.json` in its Medal Sync user data folder
(see [where settings are read from](commands.md#where-settings-are-read-from)).
Create it with the configuration wizard or `config create`, as described in
[getting started](getting-started.md#plan-configuration-before-using-live-data),
rather than by hand. This page lists every field so you can review a file or
add settings the wizard does not offer, such as more cloud copies.

`config.json` holds no passwords or tokens. SMB uses the PC's saved Windows
login, and cloud sign-in stays in the separate rclone credential file.

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
[cloud primary](../tools/medal-sync/config/medal-sync.cloud.example.json).

## Top-level fields

| Field | Default from `config create` | Meaning |
| --- | --- | --- |
| `format` | `1` | File format. Must be `1`; see [versions and schemas](versioning.md#file-formats). |
| `storageMode` | `"SERVER"` | `"SERVER"` shares the library through an SMB journal. `"LOCAL"` keeps media and journal on this PC. A file without this field uses server mode. |
| `libraryId` | Generated for a new library | 32 lowercase hexadecimal characters. Every PC sharing a library uses the same ID. |
| `accountId` | From `--account-id` | Numeric Medal account ID, as reported by `inspect`. Must match the account signed in to Medal. |
| `coordinationRoot` | From `--coordination-root` | Folder that holds the ownership journal and library generations. Server mode: a UNC share. Local mode: a dedicated local folder. |
| `remote` | From `--remote` | Primary storage. Server mode: a UNC share, or a named rclone remote and folder such as `onedrive:MedalSync`. Local mode: a dedicated local folder. |
| `captureFolder` | `"M:\\Clips"` | Folder Medal records into, on the clip drive Medal Sync mounts. The drive letter must be D to Z; it cannot be a drive root. |
| `cacheRoot` | From `--cache-root` | Local SSD folder for the clip drive's cache. Not a network drive or the clip drive. |
| `rclonePath` | The bundled `rclone.exe` | rclone executable used for the clip drive and copies. |
| `mount` | See below | Clip drive settings. |
| `maxSessionHours` | `4` | Longest session to plan for. Launch checks that this much recording fits on the cache's disk, above the free-space reserve. More than 0, at most 24. |
| `syncExclusions` | `[]` | Syncthing folders that overlap the clip folder and must ignore it. Each entry is `{ "folderId": "ID", "pattern": "/Videos/Clips" }`, with one rooted folder pattern and no wildcards. `inspect` reports whether each ignore rule is in place. Most setups leave this empty. |
| `replication` | Absent | Optional persistent local copy and cloud copies. See below. |

## `mount`: the clip drive

These become rclone mount options; `medal-sync mount-plan` prints the result.
The defaults suit most PCs.

| Field | Default | Limits | Meaning |
| --- | --- | --- | --- |
| `writeBackSeconds` | `5` | Whole seconds, above 0 | How long a finished file waits in the cache before upload starts (`--vfs-write-back`). |
| `cacheMaxGiB` | `20` | At least 1 | Soft size limit for cached files (`--vfs-cache-max-size`). New recordings stay cached until they upload, even past this limit. |
| `minFreeGiB` | `20` | At least 1 | Free space to keep on the cache's disk (`--vfs-cache-min-free-space`). Launch and downloads into the persistent copy refuse to go below it. |
| `spaceWarningGiB` | Twice `minFreeGiB`, and at least 10 above it | Above `minFreeGiB`, at most 1000000 | Free space at which the *Low disk space* notice appears while recording. See [low disk space while recording](storage.md#low-disk-space-while-recording). |
| `cacheMaxAgeSeconds` | `300` | Whole seconds, above 0 | How long unused files stay in the cache (`--vfs-cache-max-age`). |
| `cachePollSeconds` | `30` | Whole seconds, above 0 | How often rclone checks the cache for expired files (`--vfs-cache-poll-interval`). |
| `dirCacheHours` | `1000` | At least 1 | How long folder listings are cached (`--dir-cache-time`). |
| `transfers` | `2` | 1 to 8 | Uploads run in parallel (`--transfers`). |
| `controlPort` | `53682` | 1024 to 65535 | Local port for rclone's control API, on 127.0.0.1 only. `config create --spike` uses `53683`. |
| `bandwidthLimitMiB` | `null` (unlimited) | 1 to 10000 | Upload limit in MiB/s while Medal runs. See [limit upload speed while recording](storage.md#limit-upload-speed-while-recording). |
| `finishBandwidthLimitMiB` | `null` (unlimited) | 1 to 10000 | Upload limit in MiB/s after Medal exits, while uploads finish. |

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
| `fullVerifyEveryHandoffs` | `1` | 1 to 100 | Verify all media bytes every N replication passes per destination. `1` keeps always-full verification; larger values opt into incremental checks between full passes. Retries also count as passes. |
| `fullVerifyEveryDays` | `7` | 1 to 30 | Maximum age in days of a full verification when incremental checks are enabled. Missing, invalid or failed receipts always force a full pass sooner. |
| `bandwidthLimitMiB` | `null` (unlimited) | 1 to 10000 | Transfer limit in MiB/s for copies to rclone remotes: cloud targets and an rclone primary. Copies to an SMB share are not limited. |
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
