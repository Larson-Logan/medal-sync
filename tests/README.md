# Tests

Run `pwsh -File scripts/validate.ps1` on Windows with the SDK pinned in
global.json, PowerShell 7, Node 24 and Python 3.11+.

- .NET tests use synthetic SQLite fixtures and cover ownership, restoration,
  upload verification, configuration, library review, shutdown and release checks.
- Node tests exercise helper previews, scratch registry keys and temporary
  COM shortcuts. Python tests exercise a synthetic Samba configuration.
- PowerShell installer tests use temporary fake payloads and replace process
  and binary probes inside the test module. They cover corrupt archives,
  unsafe paths, pending sessions, failed copies, rollback and interruption.
- Source hygiene and local Markdown links are checked too. Synthetic Git tests
  verify that ignored agent settings are allowed locally, forced tracking is
  refused and deleting a committed instruction file does not erase its history.

No test needs a real Medal account, database, recording or server. Windows CI
runs the suite and builds a release package. Actual WinFsp mounts, playback,
two-PC round trips and physical reboot behavior need separate desktop acceptance.
