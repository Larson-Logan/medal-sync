# Changelog

## Unreleased

- Fixed: editing configuration in the setup wizard preserves required cloud
  copies. Targets can be explicitly required or optional, and Review calls out
  requirement changes and added or removed targets.
- Fixed: the setup wizard opens valid fractional session limits and displays
  hundredths of an hour. Saved numbers outside a control's display range show
  guidance and are preserved unless edited.
- Update checks now offer to run the verified installer, close the status app
  and exit the updater so Windows setup can replace the installed version.
  Deferred or failed installation keeps the download available for retrying.
  The status panel and tray menu now have **Exit Medal Sync**, with guidance
  when Medal or a library operation is still active.
- Fixed: Medal shortcuts keep Medal's default icon when the launcher repairs
  them, including pinned taskbar shortcuts without an explicit icon path.
- Fixed: cloud-copy cleanup refuses duplicate or case-conflicting paths,
  unsafe names, links and malformed listing entries with an actionable message
  before writing a review or changing the copy.
- Fixed: `replica purge-trash` lists only the trash subtree once and deletes
  exactly the batches it reports as deleting, using the same set for its final
  count. A missing trash folder is empty; batches reported as kept stay kept.

## 1.3.0 — 2026-10-30

- Every merge to `main` now publishes a release automatically, tagged
  `vMAJOR.MINOR.PATCH.NN`. `NN` is a hotfix counter: `.00` when the merge
  changes the version in `Directory.Build.props`, then `.01`, `.02` and so on.
  Update checks, the portable updater and setup compare the counter
  numerically and still recognize earlier three-part versions. This release
  is tagged with three parts, `v1.3.0`, so 1.2.0 and earlier can still find
  it. Update 1.2.0 and earlier to it before the first hotfix is published;
  after that, those versions no longer see updates and need the newest setup
  downloaded from the Releases page once. Releases are published through the private
  `medal-sync-release` GitHub App, the only non-administrator allowed to
  create `v*` tags.
- Setup can add `medal-sync` to PATH, so the CLI runs from any new terminal.
  The option is off by default. It appends the program folder's `bin`
  directory to the system or user PATH, which also exposes the bundled
  `rclone` after any copy already on PATH. Clearing it on upgrade or repair
  removes only an entry setup added; uninstall removes the entry for the
  deleted folder.
- Check cloud copies on demand with `replica verify [--target NAME] [--apply]`.
  It reads back every file of the committed generation while Ready, never
  copies, replaces or deletes, and records per-target results (counts only) in
  `replica history`. Incomplete attempts never replace the last complete result.
  Missing or different files invalidate that target's incremental receipt and
  queue an optional target for `replicate retry`.
- Clean up unreferenced files and old version history at an optional cloud copy
  with a reviewed `replica prune --target NAME --out FILE`, then
  `replica prune --in FILE --apply`. Apply holds the journal writer lock, refuses
  any change since the review and only moves files into `.medal-sync-trash/`;
  `replica purge-trash` deletes old trash separately. Referenced and recent
  files are never planned.
- Remove old generations and this PC's old recovery folders with a reviewed
  `retention --out FILE`, then `retention --in FILE --apply`. The head, recent
  ancestors, recent generations, each PC's latest generation and the
  highest-numbered folder are kept. `generations/retention.json` records what
  was removed, so numbers are never reused and `history list` shows removed
  ranges instead of damage. Update every PC to 1.3.0 before applying.
- Fixed: invalidating a cloud copy's verification receipt no longer fails when
  no receipt was ever written.
- Fixed: rclone commands no longer wait on an invisible password prompt. The
  runner disables prompts and closes stdin so encrypted configs fail promptly
  through the existing configuration guidance.

## 1.2.0 — 2026-10-29

- Retry pending optional cloud copies while Ready with
  `replicate retry [--target NAME] [--apply]`. Preview first, then copy and
  verify the committed generation without taking ownership. Launch, shutdown,
  configuration changes and a changed journal cancel the retry; unfinished
  targets stay pending. Status and the tray show progress and the result.
- Optional incremental replication verification reads back only new or changed
  media, checking unchanged files for presence and size. Private receipts are
  bound to the library, source, destination, settings, credentials and each
  file's size and modification time. Missing, damaged or failed receipts force
  a full pass. `replication.fullVerifyEveryHandoffs` defaults to 1 (always full);
  raise it up to 100 to opt in, with a full pass at least every
  `fullVerifyEveryDays` (default 7, at most 30). Same-size corruption of unchanged
  media can go undetected until the scheduled full pass. Progress and finish
  results report planned file and byte read-back counts.
- Routine Medal app and recorder updates no longer require an exact version
  pair in Medal Sync. Readable version metadata, the exact supported schema,
  stopped-process checks, native-backup integrity/sidecar/lineage checks and
  verified handoff still apply. Individually reviewed builds are identified
  separately in compatibility reports; schema-compatible future builds continue
  automatically without a version-change warning. A future change to native
  backup behavior that retains the same schema cannot be guaranteed compatible.
- Native installations now repair Medal's overwritten per-user startup hooks
  when the user previously opted into that integration. They retain its existing
  launcher target; setup alone still does not opt users in or migrate shortcuts.
- `SUPPORT.md` now asks for a `diagnostics` summary in reports. The exporter
  was checked on a real Windows PC with an active library; its preview held no
  paths, names or IDs and saved nothing.
- Add `diagnostics [--out FILE]`, a privacy-safe summary to share when asking
  for help. It reports versions, schema compatibility, installation and
  storage mode, coarse free space, ownership state and refusal codes, built
  only from allowlisted fields. Library IDs, PCs and cloud copies become
  per-export tokens; paths, names, remotes, clips, configuration, credentials,
  logs and error messages are left out, and every value is checked against
  this PC's private values before it is shown. It works without configuration
  or storage, prints a preview, and saves the same summary to a new file only
  with `--out`. Nothing is uploaded.
- Add read-only `storage check [--json]`, which checks each rclone remote's
  sign-in before you record: the credential file opens without a prompt and
  one short listing or marker read succeeds. Each remote is reported as OK,
  offline, sign in again (naming the `rclone config reconnect` command),
  configuration problem, folder missing, marker missing or another library.
  Launch runs the same check before taking ownership; outages are ignored and
  problems show as a notice in the status panel and `status`. Set the optional
  `replication.credentialCheck` to `"REFUSE"` to keep Medal closed while an
  rclone primary or required target needs attention (default `"WARN"`).
- Limit the clip drive's upload speed with `mount.bandwidthLimitMiB` while
  Medal runs and `mount.finishBandwidthLimitMiB` after it exits (both optional,
  1 to 10000 MiB/s, unlimited by default), so uploads over a VPN do not compete
  with the game. The limit switches through rclone's control API; a failed
  switch is reported and never blocks recording or finishing. `mount-plan`
  shows the `--bwlimit` flag.
- Work now lands on a new default `dev` branch, which may be briefly broken.
  Only `dev` merges into `main`, after every Windows suite passes, so `main`
  always works and is the only branch releases are built from. Branch and tag
  rulesets are kept in `.github/rulesets` and applied with
  `scripts/setup-github-rulesets.ps1`.
- `public-source.json` now records which Windows CI suites each file affects.
  Pull requests into `dev` run only those suites, so a documentation change
  runs none and a unit-test change runs only the unit tests.
  `python scripts/update-source-manifest.py --affected origin/dev` shows what a
  branch needs.
- Releases no longer need a hand-made release or tag. Merging a version bump to
  main builds, verifies and attests that commit and creates one draft release
  targeting it. Publishing the draft creates the tag.
- Each release has a milestone titled with its version. Merged pull requests
  and the issues they close join the next release's milestone automatically,
  and publishing a release closes it. A Medal Sync project board with Board,
  Triage, Releases and Pull requests views tracks planned and active work; see
  [maintainer triage](docs/triage.md#project-board).
- New [command reference](docs/commands.md) and
  [configuration reference](docs/configuration.md) list every command, exit
  code, `config.json` field, default and limit in one place.

## 1.1.0 — 2026-10-29

- Release downloads carry a GitHub build provenance attestation. Check that a
  setup EXE or ZIP was built from the tagged source with
  `gh attestation verify FILE --repo Larson-Logan/medal-sync`.
- Lower background CPU, memory and disk use. While Medal runs, the supervisor
  re-checks the Medal process it already found instead of listing every process
  each poll, and rewrites the private status file only when status changes or
  every 5 seconds (was 2). The tray reads status every 5 seconds while its panel
  is closed (every second while open). Reading and checking a library snapshot
  is about twice as fast with a third of the allocations, and a snapshot no
  longer keeps a serialized copy of the library in memory. File hashes reuse a
  pooled buffer.
- Medal Sync has its own icon on its program, shortcuts, uninstall entry and
  windows. The notification-area icon replaces the generic Windows icons and
  shows a colored dot for the current state (green Ready, red recording, blue
  working, gray waiting, amber attention needed). The icon is drawn by
  `scripts/generate-icon.py`.
- Warn while recording when the cache's disk or the persistent copy's disk runs
  low on space, again when it reaches the `mount.minFreeGiB` reserve, and when
  space returns. Folders on one disk share a notice; hysteresis and a 30-minute
  quiet period prevent repeats; a disk that cannot be measured is reported as
  unknown. Checks run in the background so a slow disk cannot delay
  supervision. The warning level defaults from the reserve and can be set with
  the optional `mount.spaceWarningGiB`.
- Add read-only `history list` and `history diff`. The list follows the
  committed head's ancestry, marks generations left by abandoned switches as
  uncommitted and reports damaged or missing history as incomplete. The diff
  counts added, removed and changed clips, deletion records and changed fields
  between two generations of the same schema; per-clip titles and paths are
  written only to a new file named with `--out`.
- `status --json` now prints a documented, versioned status document (format 1)
  instead of the raw ownership journal: the tray's phase and next step,
  `safeToSwitch`, the journal without device IDs, switch requests with their
  age, pending cloud copies and the last verification time. When storage is
  unreachable it still prints this document, with exit code 3. `status` no
  longer looks up Medal or creates this PC's identity.
- A finish that fails at primary storage or a required replica is retried with
  backoff (30 seconds up to 10 minutes) instead of every few seconds, so a
  conflict or full quota no longer restarts copy and verification continuously.
  Failures that waiting cannot fix (sign-in rejected, missing credential file,
  unreadable rclone configuration, conflicting file, full quota, failed
  verification) are reported as *Action needed* with the cause and the next
  step, instead of *Waiting for storage* or "unreachable or busy". Launch still
  starts Medal when every clip is local, and warns that the handoff will need
  the fix.
- Handoff requests older than six hours no longer prompt the owner PC. `status`
  marks them as old, and the owner's next session removes them without ever
  deleting a newer request from the same PC. Each new request notifies once.
- A log write that fails (full disk, a file held by another program) no longer
  ends session supervision while Medal runs. Unwritten lines are kept and
  written later; several Medal Sync processes can now share and rotate the log
  safely. When supervision stops while Medal is open, the launcher shows its
  message as a passive notice instead of a dialog that takes focus.

- Tighten command-line checks. A repeated option, or an option given another
  option as its value (`--reason --apply`), is now a usage error that names it.
  Session, device and library IDs must be exactly 32 lowercase hex characters
  with nothing after them, so `recover-start` reports a usage error instead of
  crashing on an ID ending in a newline. `capacity` rejects zero, negative, NaN,
  infinite and oversized numbers as usage errors.
- `status --json` exits 3 with `{"storage":"unreachable",...}` when the shared
  storage or journal cannot be reached, as plain `status` does, so scripts can
  tell an outage from a refusal.
- `abandon` previews now refuse exactly when `--apply` would: on a Ready
  library, a mistyped session ID or a blank reason.
- Clip drive health messages name the folder the drive actually serves (the
  local copy when one is set) instead of the primary storage.
- Measure free space on the disk that actually holds the cache and persistent
  copy, so disks mounted inside folders, `subst` letters and second letters for
  one disk are budgeted correctly. Refuse a FAT/FAT32 cache or persistent copy
  (files of 4 GiB or more do not fit), and a cache on a network drive, RAM disk
  or optical drive, in the setup wizard and before recording; `inventory` lists
  each storage disk, its file system and any problem.
- Report an unplugged cache or persistent-copy disk as *Waiting for storage*
  instead of an error, and refuse a first download that would leave less than
  the minimum free space before copying anything.
- Accept every rclone remote name rclone allows, including spaces, dots, `+`
  and `@`, and absolute server folders such as `sftp-home:/srv/media/Medal`.
  The setup wizard now accepts an rclone primary and checks its credential
  file, and `inventory` no longer reports an rclone primary as unreachable.
- Retry an atomic state-file replacement for about a second and a half when
  an antivirus scanner, indexer or sync client briefly holds the file, on local
  disks and SMB shares.
- Add an optional copy-and-paste agent prompt to the setup guide. It lets a
  coding agent gather this PC's details, ask the remaining questions and
  prepare a validated `config.json`, stopping before library creation,
  enrollment or startup changes.
- Retain stopped aborted-start logins separately from unfinished sessions, keeping
  unverified cache bytes intact so recovered startup failures no longer block setup.
  `finish` recovers STARTING without launching; `recover-start` previews and
  explicitly retains older orphan logins only when their cache is absent or empty.
- Harden rclone primaries: cap listing output, refuse unsafe, linked or
  duplicate remote file names, refuse a reparse-point SMB marker, keep
  pending-replica evidence when a later target fails, and fix `config create`
  and configuration review for rclone primaries. Add a cloud primary example
  config and real-rclone integration checks.
- Allow primary media storage on a named rclone remote (OneDrive, Google Drive,
  Dropbox, S3-compatible, or a personal server over SFTP/WebDAV) as well as an
  SMB share. An rclone primary records to `localCopyRoot`; the ownership
  journal stays on the server. `storage init-primary` prepares an empty primary
  so primaries can be switched.
- Hydrate only clips the primary holds; clips held only by this PC upload and
  verify at finish. Launch starts offline when every clip is already local.
- Make cloud replicas best-effort by default: an offline or failing replica is
  recorded in `replication/pending.json` and retried on the next finish while
  handoff continues. Set `"required": true` on a target to keep it blocking.
- Report storage outages as a calm "Waiting for storage" state with backoff
  retries instead of an unknown or error state. Ownership and media stay on the PC.
- Report missing or null library IDs as invalid configuration, with a field
  diagnostic and controlled refusal instead of an unhandled exception.
- Add an exact preview/apply workflow for stale persistent local media. Show
  matches to retained server history separately from unknown local changes,
  preserve replaced bytes, and retain ownership until interrupted refreshes
  resume and verify the whole reviewed file set.
- Add a native configuration wizard with local/SMB storage, optional persistent
  copies and cloud replicas, exact configuration review and explicit saving.
  Existing setups retain their identity and require Ready; changes receive a
  verified backup and atomic replacement. Enrollment remains a separate review.
- Fix `mount-plan` refusing its own `<session>` placeholder. The plan is
  validated with a sample session ID, then shown with the placeholder and a
  quoted rclone path when it contains spaces.
- Add a native tray status panel with supervisor progress, available upload
  counts, required replica, confirmation time and safe finish retry. Stale or
  unreachable status is explicit; closing the view leaves supervision intact.
- Keep repeated background notices in the status panel without taking focus.
  Windows setup includes a Medal Sync Status shortcut.
- Retain ownership when an aborted startup cannot verify that its mount stopped,
  and retry cleanup on the owning PC.
- Recheck the library after final server verification and mount shutdown before
  publishing or releasing ownership; serialize live repair and relocation with
  the session supervisor.
- Hand off intentional empty-library generations to PCs with older committed
  entries, retaining backups and refusing unshared local changes without review.
- Refuse audit and repair checkpoints from another account or an incompatible
  schema before planning changes.
- Refuse Windows device names, malformed UNC roots and separators inside appended
  path segments before accessing or replicating media.
- Keep authenticated rclone control requests on reviewed loopback endpoints,
  refuse redirects and bound status responses; failures retain unknown state.
- Audit direct and transitive NuGet dependencies during restore and fail builds
  for reported vulnerabilities at every severity.

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
