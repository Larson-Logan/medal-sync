---
type: architecture
title: Library ownership and generations
description: The one-PC-at-a-time ownership journal, immutable library generations and handoff requests that let PCs share one Medal library safely.
resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Coordination
tags: [coordination, ownership, journal, generations, handoff, google-drive, file-formats]
sources:
  - id: coordination-code
    resource: https://github.com/Larson-Logan/medal-sync/tree/dev/tools/medal-sync/MedalSync/Coordination
    title: Coordination store, ownership journal and generation store
  - id: versioning
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/versioning.md
    title: Versions and schemas, file formats
  - id: storage-guide
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/storage.md
    title: Storage choices, Google Drive cloud mode
generated: { by: "codex/gpt-6", at: "2026-11-21T07:00:00Z" }
status: draft
---

# Summary

Exactly one PC owns a library at a time. Clip records are shared between PCs;
Medal logins, microphone and encoder settings stay on each PC. Ownership lives
in a journal, and every finished session publishes an immutable **generation**
of the library. The code is under
[`tools/medal-sync/MedalSync/Coordination/`](../../../tools/medal-sync/MedalSync/Coordination/).

# Ownership journal

`OwnershipRecord` (`Ownership.cs`) is the journal, stored as `journal.json`.
Its fields are `Format`, `LibraryId`, `AccountId`, `State`, `Owner`,
`SessionId`, `CommittedGeneration`, `Revision`, `UpdatedAt` and `Reason`.

| `OwnershipState` | Status text | Meaning |
| --- | --- | --- |
| `Clean` | Ready | The last generation is committed; any PC may start a session. |
| `Starting` | | The owner is restoring its library and mounting storage. Medal is not running yet. |
| `Active` | | Medal and the owner's mount may write. |
| `Draining` | Uploading | Medal has exited; the owner is finishing uploads. |
| `Publishing` | Switching PC | The owner is committing the next generation. |
| `RecoveryRequired` | Recovery needed | Interrupted or uncertain. Only the owner, or an explicit abandon, may continue. In current code, a stopped `enroll` sets it. |

Every transition is a static function in `Ownership.cs` that checks the
current state and owner before returning the next record:

| Function | From | To |
| --- | --- | --- |
| `Acquire` | `Clean` | `Starting` |
| `MarkActive` | `Starting` | `Active` |
| `AbortStart` | `Starting` | `Clean` (start given back) |
| `BeginDrain` | `Active`, `RecoveryRequired` | `Draining` |
| `ResumeActive` | `Draining`, `RecoveryRequired` | `Active` (Medal reopened) |
| `BeginPublish` | `Draining` | `Publishing` |
| `CompletePublish` | `Publishing` | `Clean`, with the new `CommittedGeneration` |
| `CompleteUnchanged` | `Publishing` | `Clean`, no new generation |
| `RequireRecovery` | any owned state | `RecoveryRequired` |
| `Abandon` | any state but `Clean` | `Clean`; needs the exact session ID and a reason (`medal-sync abandon`) |

An abandon returns the library to the last published generation. Unpublished
clips stay on the lost PC and must be reconciled by hand. The
[session lifecycle](session-lifecycle.md) shows which phase calls each
transition.

# Coordination store (server and local modes)

`CoordinationStore` keeps the coordination folder: `library.json` (library
identity), `journal.json`, `writer.lock`, `requests/` and `generations/`.
Every transition runs under `writer.lock` and must be based on the current
`Revision` (the next record has `Revision + 1` and the same library and
account). `FreshCoordinationRead` guards against Windows reusing a cached
open of an SMB file: it compares the bytes it read with the server's directory
listing, and refuses (`StaleCoordinationReadException`) instead of deciding
from an out-of-date journal.

# Generations

`GenerationStore` writes each generation into a `.staging-*` folder, verifies
it, then renames it into place in one step, so a numbered generation folder is
always complete and interrupted staging folders are ignored. A generation
holds:

- `manifest.json`: parent, session, PC, Medal app and recorder builds, Medal
  schema version and fingerprint, file hashes and row counts.
- `library.json`: a canonical snapshot of the shared Medal tables.
- `assets.json`: media present on primary storage, and referenced media that is
  missing.

Generations are immutable. An idle **uncommitted checkpoint** may exist while a
session is `Active`. Other PCs ignore uncommitted attempts and use only the
journal's `CommittedGeneration`. `GenerationHistory` powers `history list` and
`history diff`; retention can prune old generations and records the pruned
range.

# Handoff requests

A PC that wants the library writes a `HandoffRequest` asking the owner to
finish. Launch can wait for the release with `HandoffWait`
(`Session/HandoffWait.cs`).

# Google Drive cloud mode

`CloudCoordination` with `Platform/GoogleDriveJournal.cs` replaces the SMB
folder. The format `1` journal is the `description` metadata of one Drive file
(`cloudJournalId`) and contains `identity`, `journal`, `requests`,
`storageFolderId`, `captureFolder` and `headManifestHash`. Updates are
conditional on the Drive file ETag, and the hash binds the committed head to
its verified manifest.[^versioning] Maintenance commands that need the SMB
writer lock refuse in cloud mode.

If initialization stops before generation 0 is published locally, a retry can
proceed when the cache contains only the empty generations directory or
recognized generation 0 staging directories. A published local generation 0
must still match the initializing PC and reviewed snapshot. A finish that
resumes in `Publishing` refreshes Drive history before choosing a generation
number. Even a partial remote upload reserves its number.[^coordination-code]

# Enrollment

`EnrollmentOwnership` records a pending enrollment and its review folder, so an
interrupted `enroll` can be finished with `medal-sync enroll recover`.

# Format rules

All shared files are format `1`. A reader refuses a format it does not know and
never rewrites it. Removing, renaming or changing the meaning of a field needs
a new format, a major release, and every PC updated before the next switch. See
[versions and schemas](../../versioning.md#file-formats) and the
[release runbook](../runbooks/release-and-versioning.md).

# Related

- [Session lifecycle](session-lifecycle.md)
- [Storage and replication](../systems/storage-and-replication.md)
- Tests: `CoordinationTests.cs`, `FreshCoordinationReadTests.cs`,
  `GenerationHistoryTests.cs`, `CloudHandoffTests.cs` and
  `CloudEnrollmentTests.cs` under `tests/tools/medal-sync/`

[^versioning]: Versions and schemas, file formats
[^coordination-code]: Coordination store, ownership journal and generation store
