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
    title: Storage choices, cloud mode and migration
generated: { by: "codex/gpt-6", at: "2026-11-21T23:15:53Z" }
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
| `Maintenance` | Maintenance | One PC holds a cloud lease for a reviewed maintenance operation. Other PCs wait. |
| `Migrating` | Migrating | The reviewed migration holds the source and stages a frozen destination. |
| `Migrated` | Configuration update needed | The former journal is retired. PCs using it must change configuration. |

Every transition is a static function in `Ownership.cs` that checks the
current state and owner before returning the next record:

| Function | From | To |
| --- | --- | --- |
| `Acquire` | `Clean` | `Starting` |
| `MarkActive` | `Starting` | `Active` |
| `CommitCheckpoint` | `Active` | `Active`, with a newer verified `CommittedGeneration` |
| `AbortStart` | `Starting` | `Clean` (start given back) |
| `BeginDrain` | `Active`, `RecoveryRequired` | `Draining` |
| `ResumeActive` | `Draining`, `RecoveryRequired` | `Active` (Medal reopened) |
| `BeginPublish` | `Draining` | `Publishing` |
| `CompletePublish` | `Publishing` | `Clean`, with the new `CommittedGeneration` |
| `CompleteUnchanged` | `Publishing` | `Clean`, no new generation |
| `RequireRecovery` | any owned state | `RecoveryRequired` |
| `BeginMaintenance`, `EndMaintenance` | `Clean`, `Maintenance` | Acquire or release the cloud lease at the same generation. |
| `BeginMigration`, `EndMigration` | `Clean`, `Migrating` | Freeze the source, then either return it to `Clean` on rollback or mark it `Migrated`. |
| `ActivateMigration` | `Migrating` | Destination becomes `Clean` after the source retires. |
| `Abandon` | an owned session or expired `Maintenance` | `Clean`, using the exact session or lease ID and a reason. |

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

Generations are immutable. Background sync can commit a generation while a
session is `Active`. Other PCs ignore uncommitted attempts and use only the
journal's `CommittedGeneration`. `GenerationHistory` powers `history list` and
`history diff`; retention can prune old generations and records the pruned
range.

# Handoff requests

A PC that wants the library writes a `HandoffRequest` asking the owner to
finish. Launch can wait for the release with `HandoffWait`
(`Session/HandoffWait.cs`).

# Cloud coordination

`CloudCoordination` reads and conditionally replaces one provider journal
through `ICloudJournal`. Google Drive uses the `description` metadata of a
file and its v2 ETag. OneDrive uses Microsoft Graph content and a strong item
ETag. Dropbox downloads contents and the file revision together. Every
provider checks that a stale version is rejected before changing the journal.
The configured folder ID and capture path bind the journal to its storage.
The head manifest hash binds the committed generation to its contents.[^versioning]

Cloud maintenance claims `Maintenance` from `Clean` and binds it to the
device, command and exact review. The owner can resume after an interruption.
Expiry never releases ownership. Retention publishes a hashed record with
ancestry and committed snapshot digests before removing reviewed folders.
Launch can prove a local library still matches a pruned snapshot.[^coordination-code]

Migration preserves identity and generations. It stages a frozen destination,
then verifies it, retires the source and activates the destination. The
review digest binds both journals across retries. Rollback can release the
source while it still holds the migration.[^storage-guide]

If initialization stops before generation 0 is published locally, a retry can
proceed when the cache contains only the empty generations directory or
recognized generation 0 staging directories. A published local generation 0
must still match the initializing PC and reviewed snapshot. A finish that
resumes in `Publishing` refreshes cloud history before choosing a generation
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
[^storage-guide]: Storage choices, cloud mode and migration
