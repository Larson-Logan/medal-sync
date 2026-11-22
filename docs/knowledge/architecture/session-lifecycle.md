---
type: architecture
title: Session lifecycle
description: How one Medal session moves from launch through supervision, idle sync and finish until the library is Ready for another PC.
resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Session
tags: [session, launch, supervisor, idle-sync, finish, handoff, shutdown]
sources:
  - id: session-code
    resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Session
    title: SessionCoordinator and related session types
  - id: storage-guide
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/storage.md
    title: Storage choices, background sync and shutdown
  - id: recovery-rules
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/getting-started.md
    title: Getting started, recovery rules
generated: { by: "codex/gpt-6", at: "2026-11-21T23:15:53Z" }
status: draft
---

# Summary

A session is one PC's period of ownership of the shared Medal library. The
`SessionCoordinator` partial class drives it in four phases, each in its own
file under
[`tools/medal-sync/MedalSync/Session/`](../../../tools/medal-sync/MedalSync/Session/).
Each phase moves the shared ownership journal through the states described in
[library ownership](library-ownership.md).

| Phase | File | Entry point | Journal states |
| --- | --- | --- | --- |
| Launch | `SessionCoordinator.Launch.cs` | `Launch()`, `LaunchWhenReleased(HandoffWait)` | `Clean` → `Starting` → `Active` |
| Supervise | `SessionCoordinator.Supervise.cs` | `Supervise(session, notify, cancellation)` | `Active` |
| Idle sync | `SessionCoordinator.Idle.cs` | `SyncWhileIdle(...)`, called by the supervisor | `Active` (no transition) |
| Finish | `SessionCoordinator.Finish.cs` | `Finish(session)` | `Draining` → `Publishing` → `Clean` |

A failed or uncertain finish does not release the library: the journal stays
in its current state with this PC as owner, and the supervisor retries. Only
this PC, or an explicit `abandon` from another PC, can move it on. The
`RecoveryRequired` state comes from a stopped enrollment, not from a failed
finish; finish can resume from it.

# Launch

When the journal is `Maintenance`, launch waits and identifies the PC and
command. A migration in progress blocks launch. A retired journal tells a PC
to configure the migrated destination.[^session-code]

If this PC still owns its previous session, launch attempts to finish it
first. When the attempt needs a retry, launch returns
`FinishingPreviousSession` and the supervisor continues the handoff. If
Medal reopens, Medal Sync checks that this PC still owns the active session and
that the drive is healthy before resuming normal supervision.[^session-code]

1. A read-only preflight (`LaunchPreflight`) runs before ownership is taken. A
   `Refusal` keeps Medal closed and the library Ready; a `Warning` starts Medal
   with a passive notice.
2. The coordinator acquires ownership (`Ownership.Acquire`, `Clean` →
   `Starting`), restores the committed library generation, starts the clip
   drive (see [storage and replication](../systems/storage-and-replication.md)),
   and checks that the media the library needs is reachable through it.
3. It then marks the session `Active` (`Ownership.MarkActive`) and starts Medal.
4. If a step fails, `GiveBackStart` stops the clip drive and returns the
   journal to `Clean` (`Ownership.AbortStart`), keeping the aborted start's
   evidence on this PC. If the drive cannot be confirmed stopped, ownership and
   the cache stay on this PC so a later launch can retry the cleanup.
5. The other launch results are `Started`, `AlreadyRunning`, `Waiting`
   and `Blocked`.

# Supervise and idle sync

While Medal runs, the supervisor polls status, publishes progress to
`desktop-status.json` for the tray, and checks free disk space every 30
seconds.

Background sync runs inside the supervisor, serialized with finish. It copies
settled, library-referenced media while CPU use is below 20 percent and Medal
is not recording.[^storage-guide] Transfer guards check for recording, high CPU
use, Medal exit and shutdown. Once everything verifies, the worker commits a
generation and keeps the session `Active`. Background work never deletes local
clips or releases ownership.

# Finish

`Finish` moves the journal to `Draining` (`Ownership.BeginDrain`, from `Active`
or `RecoveryRequired`), drains rclone's upload queue, and verifies primary
storage and required replicas. It then moves to `Publishing`
(`Ownership.BeginPublish`) and either commits the next generation
(`CompletePublish`) or, when the library did not change, returns to `Clean`
without one (`CompleteUnchanged`). If Medal is opened again before publishing,
`Ownership.ResumeActive` returns the session to `Active`. Finish returns a
`FinishOutcome`:

- `FinishStatus`: `Published`, `Unchanged`, `Pending` or `Blocked`.
- `PendingCause`: `None`, `StorageOffline`, `Failed` or `NeedsAction`. Every
  cause except `None` retries with backoff: 30 seconds up to 5 minutes while
  storage is offline, and up to 10 minutes after other failures.
  `NeedsAction` shows as *Action needed* in the tray.

Finish reuses verification receipts only when the media match. The SSD cache
and the session's Medal login are removed only after the generation commits;
the login first moves to `finished-sessions/SESSION/` with a receipt.[^recovery-rules]

# Keeping the PC awake

`IKeepAwake` (`Platform/KeepAwake.cs` on Windows, a `PowerRequestSystemRequired`
power request) is held for the whole of each `Finish` call, by the supervisor
across finish attempts while uploads are still running (`PendingCause.None`),
during an idle sync transfer and during `replicate retry --apply`. It is
released on Ready, a refusal, every retry backoff, a reopened Medal and any
exception. A refused request is logged once and changes nothing else.
`keepAwakeWhileFinishing: false` disables it.

# Windows shutdown

`SessionShutdown.cs` and `WindowsSessionEnd.cs` give finish Windows' 25-second
shutdown budget. Windows ends rclone, a console program, before it notifies this
lowest-level supervisor, and a process started during session end fails with
0xC0000142. Finish therefore never restarts the drive at shutdown. A stopped
drive whose cache holds no rclone item marked `Dirty` (only read copies, such as
the library marker) needs no write-back, so finish goes straight to the final
verification. Otherwise finish returns Pending at once and the next sign-in
resumes the uploads from the same cache.
If finish is interrupted, the journal retains the most recent committed
generation and the original PC retains its cache. An operator who knows the
original PC fully shut down can switch to that generation from the launch dialog.

# Invariants to preserve

- An unknown upload state is never success.
- Unpublished, uncertain or unreviewed work keeps ownership and the cache.
- Only one writer runs at a time: idle sync and finish never overlap.
- Changes here need tests for refusal and recovery as well as success, under
  `tests/tools/medal-sync/` (`Session*Tests.cs`, `IdleMediaSyncTests.cs`,
  `WindowsShutdownTests.cs`, `HandoffWaitTests.cs`).

# Related

- [Library ownership and generations](library-ownership.md)
- [Storage and replication](../systems/storage-and-replication.md)
- User-facing recovery rules: [getting started](../../getting-started.md#recovery-rules)
- User-facing sync behaviour: [background sync and shutdown](../../storage.md#background-sync-and-shutdown)

[^storage-guide]: Storage choices, background sync and shutdown
[^recovery-rules]: Getting started, recovery rules
[^session-code]: SessionCoordinator and related session types
