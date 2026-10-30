# Tests

Run `pwsh -File scripts/validate.ps1` on Windows with the SDK pinned in
global.json, PowerShell 7, Node 24 and Python 3.11+. To rerun one area, pass
`-Part` with `Unit`, `Desktop`, `Scripts` or `Repository` (for example
`-Part Unit, Desktop`); CI runs these as parallel jobs.

- .NET tests use synthetic SQLite fixtures and cover ownership, restoration,
  upload verification, configuration, library review, shutdown and release checks.
- Native desktop checks in `tests/desktop/` use a fake supervisor and isolated
  event names. They exercise every displayed state, queue/replica text, accessible
  controls, stale status, disconnect/reconnect and closing the panel. No live
  configuration, Medal process, media, journal or storage is read or changed.
- Node tests exercise helper previews, scratch registry keys and temporary
  COM shortcuts. Python tests exercise a synthetic Samba configuration.
- PowerShell installer tests use temporary fake payloads and replace process
  and binary probes inside the test module. They cover corrupt archives,
  unsafe paths, pending sessions, failed copies, rollback and interruption.
- Source hygiene and local Markdown links are checked too. Synthetic Git tests
  verify that ignored agent settings are allowed locally, forced tracking is
  refused and deleting a committed instruction file does not erase its history.
  The history audit also covers replaced credentials, runtime files, private
  deployment references, commit/tag metadata and pull-request-only refs in a
  bare mirror. A shallow clone cannot claim a complete history audit.
- Windows CI also installs the native setup into a disposable per-user folder,
  verifies registration and shortcuts, tests upgrade and active-session refusal,
  then uninstalls and checks that an unowned file was preserved. This test refuses
  to run outside an ephemeral GitHub-hosted runner.

Run `pwsh -File scripts/test-storage-integration.ps1` to exercise real pinned
rclone processes and WinFsp mounts with disposable synthetic libraries. It tests
named remotes for all three provider slots, copy verification, conflict refusal,
versioned updates, interruption/retry, persistent-local retention, local mode
without SMB and a sequential two-PC simulation. The mount scenarios also
exercise real compatibility guards with synthetic version manifests: unreadable
update metadata and unsupported schemas refuse launch; a blocked finish drains
uploads while retaining ownership/cache, then recovers automatically when readable
future app/recorder versions and the supported schema are available.
Named test remotes alias local folders; no cloud service or account is contacted.
WinFsp must already exist on a personal PC; driver installation is restricted
to ephemeral hosted CI.

After building packages, `pwsh -File scripts/test-windows-installer-isolated.ps1`
tests native installation, bundled dependencies, shortcuts, repair, refusal and
uninstall on a personal Windows PC. It builds a uniquely identified setup with
its own registration, group, mutexes and synthetic readiness probe. Production
payloads remain unchanged; no production flag bypasses readiness. Logs and a
verification receipt stay under the ignored `.validation/` directory.

These checks include the bundled uninstaller EXE/log, update shortcut, damaged
CLI repair, program-folder guards and a readiness change before uninstall removal.
The .NET release tests cover setup download verification, cancellation and cleanup.
`dotnet run --project tests/integration -- --update-progress` exercises the native
Shell progress dialog with an in-memory synthetic download and no network request.

No test needs a real Medal account, database, recording or server. Real provider
account behavior, playback in Medal, physical PCs and reboot acceptance remain
separate from these reproducible integration checks.
