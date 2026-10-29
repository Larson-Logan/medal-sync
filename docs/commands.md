# Command reference

Medal Sync installs two programs in its `bin` folder:

- `medal-sync-launch.exe` is the desktop launcher. The Start menu shortcuts,
  the status tray and Medal's startup entry use it. It has no console window.
- `medal-sync.exe` is the command-line companion. Use it to configure, check
  and recover a library. `medal-sync --help` prints a short summary.

The examples assume `$cli` names `medal-sync.exe` in the folder you chose in
setup, as in [getting started](getting-started.md#plan-configuration-before-using-live-data).

## How commands behave

- **Preview, then apply.** A command that changes anything prints what it
  would do and changes nothing until you repeat it with `--apply`.
- **Strict options.** An unknown option, a repeated option or an option the
  command does not use is a usage error. Nothing is guessed or ignored.
- **No overwriting.** Files named with `--out` must not exist yet. Commands
  never replace a configuration, snapshot, review or report.
- **Medal must be quit** for `init`, `enroll`, `repair`, `relocate`,
  `refresh-media` and `promote`. Quit it normally from its tray icon.
- **One at a time.** Commands that change state refuse while the supervisor or
  another state-changing command is running.
- **Blocked during setup.** While Windows setup or uninstall is open, every
  command except `version` and `setup check` refuses.
- **Windows only**, except `version`, `config create`, `config validate`,
  `snapshot`, `dry-run-restore`, `audit`, `repair` and `capacity`, which also
  run elsewhere for testing.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Done, or the check passed. |
| `1` | Refused or blocked. Nothing unsafe was done; the reason is on standard error. |
| `2` | Usage error, such as a missing or unknown option. |
| `3` | Waiting: storage or the server could not be reached, or work is still pending. Try again later. |

### Where settings are read from

Commands use the Medal Sync user data folder (the *state root*). It holds
`config.json`, `device.json`, logs, backups and session receipts. The first
of these that applies is used:

1. A portable installation whose parent folder of `bin` already holds
   `config.json` and `device.json` uses that folder. Windows setup
   installations skip this step.
2. `%USERPROFILE%\.medal-sync`, if it holds `config.json` or `device.json`.
3. `%LOCALAPPDATA%\MedalSync`, if it holds either file.
4. `%LOCALAPPDATA%\LarsonServer\MedalSync`, if it holds either file.
5. Otherwise `%USERPROFILE%\.medal-sync`, for a new setup.

`inspect` reports the state root it resolved. An unconfigured desktop launch
shows it too.

## Desktop launcher

| Command | What it does |
| --- | --- |
| `medal-sync-launch.exe` | Checks storage and ownership, starts Medal, supervises the session and hands off after Medal exits. Waits up to 20 seconds for the server. Offers the configuration wizard when Medal Sync is not set up. |
| `medal-sync-launch.exe --at-sign-in` | The same, as used by Medal's startup entry. Waits up to 2 minutes for the server, and exits quietly when Medal Sync is not set up. |
| `medal-sync-launch.exe medal://...` | Opens a Medal link through the same checks, or passes it to the running session. |
| `medal-sync-launch.exe --configure` | Opens the configuration wizard. |
| `medal-sync-launch.exe --status` | Opens the status panel. |
| `medal-sync-launch.exe --tray` | Starts the status icon without opening the panel. |
| `medal-sync-launch.exe --check-updates` | Checks for a newer release and offers a verified download. See [updates](updates.md#upgrade-and-repair). |

## Check this PC (read-only)

| Command | What it does |
| --- | --- |
| `version` | Prints the Medal Sync version and the supported Medal schema. |
| `compatibility [--json]` | Checks the installed Medal app, recorder and database schema. Works before configuration. Exit 1 means a check failed or could not run. |
| `inspect [--json]` | Inventory of this PC: Medal versions and account ID, schema support, capture folder, drives and free space, whether the clip drive letter is free, startup entries, Syncthing folders, rclone and WinFsp, server reachability and the state root. Lists anything that blocks setup. |
| `config validate --config FILE` | Checks a configuration file's syntax and paths. It does not contact Medal or storage. |
| `capacity --bitrate MBPS --hours H --free-gb GB [--reserve-gb GB]` | Works out whether a session of `H` hours at `MBPS` megabits per second fits in `GB` gigabytes of free space, keeping a reserve (default 20 GiB). Exit 1 means it does not fit. |
| `mount-plan` | Prints the rclone mount command Medal Sync will use, without credentials. |
| `setup check [--state-root DIR] [--out FILE]` | Checks that programs can be installed or removed now: nothing running, no unfinished session and a Ready library. Setup runs this for you. |

## Check the library (read-only)

| Command | What it does |
| --- | --- |
| `status [--json]` | Who owns the library, the current phase and whether it is safe to switch PCs. The JSON fields are listed under [status for scripts](getting-started.md#status-for-scripts). |
| `history list [--json] [--limit N] [--before N]` | Published generations, newest first. `--limit` is 1 to 1000 (default 20). See [library history](storage.md#library-history). |
| `history diff --from N --to M [--json] [--out FILE]` | Counts changes between two generations. `--out` writes per-clip titles and paths to a new private file. |
| `snapshot export --out FILE [--db DB]` | Writes the library tables to a new file. It contains clip titles and paths: keep it private. |
| `snapshot verify --in FILE` | Checks a snapshot file and whether its schema is supported. |
| `dry-run-restore [--in FILE] [--db DB]` | Restores a snapshot (or the current library) into a disposable copy and verifies the result. The live database is only read. |
| `audit --checkpoint FILE [--db DB] [--json]` | Compares the library with an earlier snapshot and flags clips that lost their paths or disappeared without a deletion record. Exit 1 means it found some. |

`--db` names a `medal-ACCOUNT.db` file to use instead of the signed-in Medal
library, for example a copy.

## Set up a library

| Command | What it does |
| --- | --- |
| `config create ... --out FILE [--apply]` | Previews, then creates, a new configuration file. Options are listed below. |
| `init [--apply]` | Creates a new shared library from this PC as generation 0. Every clip must already be on primary storage. Only for a new library; never run it against an existing journal. |
| `enroll --out DIR [--apply]` | Adds this PC to an existing library. The preview writes a review of the merged library to `DIR`; apply uses that exact review. |
| `enroll recover --out DIR [--apply]` | Restores the merged library and this PC's original device settings from an interrupted enrollment's review. Run `finish` afterwards. |
| `storage init-primary [--apply]` | Marks a new, empty primary folder or rclone remote for this library, so you can [switch primaries](storage.md#switch-primaries). Needs `replication.localCopyRoot` and a Ready library. |

### `config create` options

| Option | Meaning |
| --- | --- |
| `--account-id ID` | Your numeric Medal account ID, from `inspect`. Required. |
| `--new-library` or `--library-id ID` | Exactly one. Use `--library-id` with the existing ID when adding another PC. |
| `--cache-root DIR` | Local SSD folder for the clip drive's cache. Required. |
| `--remote PATH` | Primary storage: a UNC share, a named rclone remote and folder, or in local mode a local folder. Required. |
| `--coordination-root PATH` | Folder for the ownership journal. A UNC share in server mode (required); in local mode it defaults to `coordination` in the state root. |
| `--storage-mode server\|local` | Default `server`. |
| `--capture-folder PATH` | Folder Medal records into on the clip drive. Default `M:\Clips`. |
| `--rclone-path FILE` | rclone executable. Defaults to the bundled copy beside `medal-sync.exe`. |
| `--local-copy-root DIR` | Keep a persistent local copy of the collection. |
| `--cloud-remote NAME:FOLDER` | Add one cloud copy. Needs `--rclone-config`. Add more in the JSON. |
| `--rclone-config FILE` | rclone credential file for cloud copies or an rclone primary. |
| `--replica-conflicts stop\|version` | What to do when a cloud copy already has a different file. Default `stop`. |
| `--sync-folder ID [--sync-pattern PATTERN]` | A Syncthing folder that must ignore the clip folder. Default pattern `/Videos/Clips`. |
| `--spike` | Create a test configuration for a disposable folder whose name contains `spike`. |

Each option maps to a field in `config.json`; see the
[configuration reference](configuration.md).

## Run and finish sessions

| Command | What it does |
| --- | --- |
| `launch [--wait-for-server SECONDS] [--medal-link URL] [--replace-local-library]` | Starts Medal through Medal Sync and supervises it until it exits, then hands off. `--wait-for-server` is 0 to 600 seconds. The desktop launcher normally runs this for you. |
| `finish [--accept-library-changes]` | Finishes this PC's interrupted session: drains uploads, verifies storage, publishes and releases ownership. Exit 3 means storage is still unavailable. `--accept-library-changes` approves the clip removals that finish listed; review them first. |

`launch --replace-local-library` is only for a PC whose Medal library has
changes that were never shared, usually because Medal was opened without Medal
Sync. Launch refuses such a library and names this option. Keep a copy first
with `snapshot export`; the replacement also keeps a backup.

## Recover

Read [recovery rules](getting-started.md#recovery-rules) before using these.

| Command | What it does |
| --- | --- |
| `recover-start --session ID [--apply]` | Keeps the login of an orphaned older session when its cache is empty. |
| `refresh-media --out FILE` | Reviews differences between the persistent local copy and primary storage. See [review and refresh persistent local media](storage.md#review-and-refresh-persistent-local-media). |
| `refresh-media --in FILE --apply [--accept-unknown-media]` | Applies that exact review, keeping a backup of every replaced file. |
| `repair --checkpoint FILE [--db DB] [--apply]` | Puts back clips Medal dropped since a snapshot, when their files still exist. Takes a database backup first. |
| `relocate --from DIR [--config FILE] [--apply]` | Copies library files left outside the capture folder to the server's clip folder, verifies them and repoints the library. The originals stay. SMB primary only. |
| `abandon --session ID --reason TEXT [--apply]` | Gives up a session whose PC is lost and returns the library to the last published generation. Unpublished clips stay on that PC and must be reconciled by hand. |

## Test mounting before using live data

| Command | What it does |
| --- | --- |
| `spike start\|status\|stop --config FILE` | Mounts a disposable test folder whose name contains `spike`, using the real mount code. It never touches the journal, the library database or Medal's settings. |
| `promote --spike-config FILE --config FILE --server-ignore FILE [--apply]` | Verifies a stopped spike folder and renames it for production use on the same share. |
