---
type: system
title: Storage and replication
description: Storage modes, the rclone and WinFsp clip drive, settled-media and disk-space rules, and how copies to primary storage and cloud replicas are verified.
resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Storage
tags: [storage, rclone, winfsp, vfs-cache, replication, verification, disk-space]
sources:
  - id: storage-code
    resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Storage
    title: Storage planning, readiness and verification
  - id: platform-code
    resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Platform
    title: rclone mount, replication and replica maintenance
  - id: storage-guide
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/storage.md
    title: Storage choices
  - id: configuration
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/configuration.md
    title: Configuration reference
generated: { by: "codex/gpt-6", at: "2026-11-21T23:15:53Z" }
status: draft
---

# Summary

Medal records to a virtual clip drive. The drive is an rclone mount over WinFsp
with a VFS cache on a fast local disk. Media then reaches primary storage and
optional cloud replicas, and is verified before the library can be handed off.
Planning and checks are in
[`tools/medal-sync/MedalSync/Storage/`](../../../tools/medal-sync/MedalSync/Storage/);
the code that runs rclone is in
[`tools/medal-sync/MedalSync/Platform/`](../../../tools/medal-sync/MedalSync/Platform/).

# Storage modes

| Mode | Where Medal writes | What stays after handoff |
| --- | --- | --- |
| Server with SSD cache | SSD-backed clip drive, uploaded to SMB | Server media; the session cache is removed |
| Local copy and server | Clip drive backed by a persistent local folder | Local media and server media |
| Local storage | Clip drive backed by a persistent local folder | Local media and optional cloud copies |
| Cloud or personal-server primary | Clip drive backed by a persistent local folder | Local media and the rclone primary |
| Cloud mode | Clip drive backed by a persistent local folder | Local media, cloud media and library history |

Configuration lives in each PC's `config.json` (`mount` and `replication`
sections); see the [configuration reference](../../configuration.md). Ownership
for each mode is described in [library ownership](../architecture/library-ownership.md).

# Clip drive

- `MountPlan` builds the rclone mount command (`mount-plan` shows it), including
  the bandwidth limit (`mount.bandwidthLimitMiB`, default 2 MiB/s;
  `mount.finishBandwidthLimitMiB` after Medal exits). The limit is written as
  `NMi` so rclone throttles local/SMB write-back and uncached reads. Credentials for the rclone
  control API are passed in the environment, never as arguments.
- `RcloneClipMount`, `RcloneControl` and `RcloneRunner` start, control and stop
  rclone. `VfsStatus` reads its upload queue.
- The VFS cache writes closed files back after `mount.writeBackSeconds`.

# Readiness and disk space

- `StorageVolume` and `StorageReadiness` measure the disk that actually holds
  each folder. They refuse a cache on network drives, RAM disks or optical
  drives, and refuse FAT/FAT32 for the cache or persistent copy.
- `CapacityAdmission` refuses a launch or download that would leave less than
  `mount.minFreeGiB` free.
- `SpacePressure` raises *Low disk space* and *At the reserve* notices while
  recording. These are warnings only: nothing is deleted and Medal is never
  closed.

# Settled media

`SettledMedia` copies a file only when its size and modification time are
unchanged across observations at least 30 seconds apart. Only media referenced
by the library is copied, never arbitrary cache files, database backups or
credentials.[^storage-guide]

# Replication and verification

- `ReplicationPlan` selects only known media references. Then
  `Platform/StorageReplication.cs` runs `rclone copy` to immutable destinations
  followed by `rclone check`, which reads the files back with `--download`
  (`Platform/ReplicaRemote.cs`). Cloud mode verifies both media and
  generations. Google Drive lists MD5 hashes for history cache checks,
  while OneDrive and Dropbox use byte readback.[^platform-code]

Cloud maintenance holds a lease while retention, primary or replica cleanup,
or persistent-media refresh applies. A migration verifies media and generations
on Google Drive before retiring its original ownership journal.[^storage-guide]
- `ReplicationVerification` keeps private per-destination evidence of what
  verified. Incremental verification reuses it, and removing it forces a full
  pass.
- Primary storage and replicas with `"required": true` must verify before
  handoff. Optional replicas that cannot be reached are recorded in
  `replication/pending.json` and retried later (`ReadyReplicaRetry`).
- Full verification is the default. `replication.fullVerifyEveryHandoffs`
  enables incremental verification.
- rclone's error text is never stored or shown, only the kind of failure
  (`RcloneFailure`), because it can contain credentials or private file names.
- While the library is Ready:
  - `ReplicaAudit` is a verify-only check of replicas against the committed
    generation. It never copies, deletes or takes ownership, and never replaces
    the fresh verification before handoff.
  - `ReplicaPrune` removes unreferenced replica files in two reviewed steps:
    move them to `.medal-sync-trash/`, then purge old trash.
  - `ReplicaCheckStatus` keeps advisory check history.
- `StoragePromotion` (`medal-sync promote`) verifies a stopped spike folder and
  renames it for production on the same share. It hashes every file and deletes
  nothing. Switching to a different primary is a configuration change followed
  by `medal-sync storage init-primary`; see
  [switch primaries](../../storage.md#switch-primaries).

# Related

- [Session lifecycle](../architecture/session-lifecycle.md): when idle sync and
  finish run these copies.
- User guide: [storage choices](../../storage.md) and
  [verification, conflicts and recovery](../../storage.md#verification-conflicts-and-recovery)
- Tests: `Storage*Tests.cs`, `Replica*Tests.cs`, `ReplicationTests.cs`,
  `SpacePressureTests.cs` and `IncrementalVerificationTests.cs` under
  `tests/tools/medal-sync/`, and the real rclone/WinFsp suite
  `scripts/test-storage-integration.ps1`

[^storage-guide]: Storage choices
