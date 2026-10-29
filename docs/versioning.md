# Versions and schemas

Medal Sync tracks three kinds of version: the program's release version, the
`format` number inside each file it writes, and the Medal database schema it
supports. They change independently. This page says what each one means and
when it changes.

## Release version

`Directory.Build.props` is the single source of the version. Both programs,
setup, the portable ZIP and `release.json` carry it. Releases use
`MAJOR.MINOR.PATCH` and are tagged `vMAJOR.MINOR.PATCH`, for example `v1.1.0`.
See the [release process](releases.md) for how a tag becomes a release.

| Part | Raised when |
| --- | --- |
| `MAJOR` | A PC on the new version can no longer share a library with a PC on the previous major version, a documented file `format` changes, a documented command or option is removed, or a documented output field changes meaning. |
| `MINOR` | New commands, options, settings or output fields; newly supported Medal builds or schemas; behavior changes that keep existing configurations and libraries working. |
| `PATCH` | Fixes and documentation only, with no new options or fields. |

Only stable tags count. Automatic update checks ignore drafts, prereleases and
any tag that is not exactly `v` followed by three numbers without leading
zeros. The installed version is compared numerically, so `1.10.0` is newer than
`1.9.0`. `medal-sync version` prints the installed version together with the
supported Medal schema.

Undocumented output is not a contract. For example, `status --json` printed the
raw ownership journal before 1.1.0; it now prints the documented status
document, so that change shipped in a minor release.

## File formats

Every JSON file Medal Sync writes has a top-level `format` number. All current
files are format `1`. A reader that finds a different number refuses the file
instead of guessing; it never rewrites or upgrades a file it does not
understand. Adding optional fields keeps the same format. Removing a field,
renaming it or changing what it means needs a new format and a major release.

### Shared library (server or local coordination folder)

All PCs that share a library read these, so a new format here also needs every
PC to update before the next switch.

| File | Contents |
| --- | --- |
| `library.json` | Library identity: library ID, Medal account ID, creation time and creating PC. |
| `journal.json` | Ownership journal: state, owner, session, committed generation and revision. |
| Generation `manifest.json` | One immutable generation: parent, session, PC, Medal app and recorder builds, Medal schema version and fingerprint, file hashes, row counts and the Medal Sync version that wrote it. |
| Generation `library.json` | Canonical snapshot of the shared Medal tables, with the schema version and fingerprint it was read from. |
| Generation `assets.json` | Media present on primary storage, and media the library references but that is missing. |
| Handoff requests | A PC asking the owner to finish its session. |

### This PC only

| File | Contents |
| --- | --- |
| `config.json` | Storage, cache, replica and mount settings. Every field is listed in the [configuration reference](configuration.md). |
| `device.json` | This PC's identity and display name. Never shared. |
| `replication/pending.json` | Optional cloud copies waiting for a retry. |
| `replication/storage-check.json` | The latest `storage check` result for each rclone remote, without rclone's error text. |
| `desktop-status.json` | Progress the supervisor publishes for the tray. |
| `setup-layout.json` | Marker that the programs were installed by Windows setup. |
| Enrollment, refresh and recovery receipts | Reviewed plans and evidence kept while an operation is in progress. |

### Command output

| Command | Document |
| --- | --- |
| `status --json` | Status for scripts. Fields are listed under [status for scripts](getting-started.md#status-for-scripts). |
| `history list --json`, `history diff --json` | Generation history and change counts. See [library history](storage.md#library-history). |
| `release.json` in the portable ZIP | Version, runtime and per-file SHA-256 hashes. See [package verification](updates.md#package-verification). |

Later versions of a format only add fields, so scripts should ignore fields they
do not recognize and check `format` before reading anything else.

## Medal database schema

Medal Sync reads Medal's own library database, whose schema Medal controls. A
supported schema is identified by three values, all of which must match:

| Value | Where it comes from | Supported |
| --- | --- | --- |
| `user_version` | SQLite `PRAGMA user_version` | `4` |
| Counter definitions version | `content_counts.__defsVersion` | `3` |
| Schema fingerprint | SHA-256 of the normalized table and column definitions | One reviewed fingerprint, from Medal 2643.583.1 |

`medal-sync version` and `compatibility --json` report the schema found on this
PC and whether it is supported. A Medal update that changes any of the three
values is refused before launch and before publishing: ownership and cache stay
on the PC until a Medal Sync release adds the new schema. Supporting a new
schema is a minor release; dropping one is a major release.

Each generation records the schema it was read from. `history diff` refuses to
compare generations from different schemas. Enrollment allows PCs to run
different reviewed Medal app builds only when their schemas match exactly; see
[requirements and limits](getting-started.md#requirements-and-limits) and
[status for scripts](getting-started.md#status-for-scripts) for the reviewed builds.
