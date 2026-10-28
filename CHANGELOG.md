# Changelog

## Unreleased

## 1.0.0 — 2026-10-28

- Fresh Medal build/schema checks before launch and publication, with automatic
  update warnings during sessions and a configuration-independent
  `compatibility --json` report. Incompatible updates retain ownership and cache
  while uploads drain. Reviewed updates publish their current build versions.
- Enrollment permits different reviewed compatible Medal app builds with the
  pinned recorder and an identical supported schema. Version files are read
  independently, and changed counter columns report unsupported schemas.
- Native update shortcut checks stable releases before configuration and offers
  a cancelable setup download with size and SHA-256 verification.
- Setup explains install, repair and upgrade actions, repairs damaged CLI files
  using the registered version, and guards against accidental program relocation.
- The bundled uninstaller explains retained data and rechecks readiness after
  confirmation. Native integration checks its EXE, log and Windows registration.
- Portable changes block native setup and library startup, refuse native program
  layouts, and accept stable release tags with or without the `v` prefix.
- Unconfigured desktop launches show the expected configuration location and
  setup guidance before probing Medal. Sign-in launches exit quietly until setup.
- Reopening an already supervised Medal window no longer waits for a release
  check. Busy-session messages explain that upload and verification must finish.
- First-launch Medal links travel through the guarded startup, replacing delayed
  background forwarding. Repeated sign-in launches do not reopen Medal or display
  busy-session dialogs.
- Windows process-start failures explain how to retry while retaining the session
  and cache. Invalid sync exclusion entries and oversized cache durations report
  configuration errors instead of crashing.
- Expanded GitHub issue forms for bugs, features, setup questions and documentation.
- Public support, contribution and moderation guidance, with maintainer triage
  labels and a pull request template that records validation and recovery behavior.
- Grouped monthly minor/patch dependency updates, integration-project coverage
  and label-based generated release notes.

## 0.4.0 — 2026-10-27

- Verified native-backup compatibility for Medal 2644.591.1 with recorder
  2643.3040.1; unknown app/recorder pairs remain refused.

- Native setup bundles pinned rclone and its license, and offers the pinned
  WinFsp MSI for all-users installation. Dependency downloads are verified.
- Local mode supports a persistent collection and cloud replicas without SMB.
- Optional replica versioning preserves prior destination files before updates;
  configurable bandwidth limits support slower connections.
- Reproducible integration tests run real rclone transfers, cancellation/retry,
  real WinFsp mounts, sequential synthetic PCs and uniquely identified native setup.

- Generic `MedalSync` code namespace and profile-based defaults, with existing
  configurations, identities and compatibility mutexes retained.
- Optional persistent local recording storage with verified upload to SMB.
- Required OneDrive, Dropbox and Google Drive media replicas through named
  rclone remotes, exact file lists and byte verification before handoff.
- Replica failures, conflicts and shutdown interruption retain ownership and
  media; successful verification produces a private receipt.
- Setup storage guidance, WinFsp detection, per-user installation selection
  and an offline guide available from the Start menu.
- Expanded storage documentation, GitHub overview and credential-file hygiene.

## 0.3.0 — Unreleased

- Native Windows setup EXE with a Program Files destination, Start menu and
  optional desktop shortcuts, in-place upgrades and Installed apps uninstallation.
- Writable per-user state stays separate from program files; existing physical
  profile and AppData configurations are retained without copying identities.
- Setup verifies running applications and library readiness before installation,
  and prevents new library operations while installation or uninstall is open.
- Machine-wide installation owns its shortcuts and preserves existing portable
  Medal startup integration. It never automatically closes Medal or reboots.
- GitHub releases publish the standard setup EXE alongside the advanced portable ZIP.

## 0.2.0 — Unreleased

- Versioned Windows x64 packages with manifests and SHA-256 checksums.
- One shared version for both self-contained executables.
- GitHub CI for pull requests and main; tagged builds create draft releases.
- Installer, updater and uninstaller with backups and recovery receipts.
- Automatic public GitHub release checks on desktop launch.
- Public source export, generic examples and contributor guides.
- Unconfigured inventory no longer probes a developer's server address.

## 0.1.0

Initial companion with SSD-cached storage, serial library handoff, guarded
database restore, removal review, Windows launcher and shutdown handling.
