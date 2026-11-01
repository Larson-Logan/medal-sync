# Installation, updates and removal

Download `medal-sync-VERSION-setup-win-x64.exe` from
[GitHub Releases](https://github.com/Larson-Logan/medal-sync/releases) once a
maintainer publishes a release. This is the normal Windows installer.

## Install

1. Run the setup EXE and choose installation for all users or just your account.
   The all-users option requires Windows administrator approval.
2. Review storage guidance, dependency status, license and program folder.
   All-users setup defaults to `C:\Program Files\Medal Sync`; per-user setup
   uses your local Programs folder.
3. Choose whether to add a desktop shortcut and whether to add `medal-sync` to
   PATH, then select **Install**. The PATH option lets you type `medal-sync` in
   any terminal opened after setup. It adds the program folder's `bin` directory
   to the end of the system PATH (all users) or your user PATH (just you), so
   the bundled `rclone` is on PATH too, after any copy you already had.
4. Read **Medal Sync Setup Guide** from the completion page or Start menu,
   then open **Medal Sync** once configuration and enrollment are complete.

The installer includes the self-contained companion, .NET runtime and pinned
rclone executable with its notices. All-users setup offers installation of the
bundled pinned WinFsp driver when it is missing. Per-user setup reuses an existing
WinFsp installation or needs an administrator to install that driver once.
Setup never automatically restarts Windows. Ordinary installation needs neither
PowerShell nor a separate .NET installation. Follow
[getting started](getting-started.md) for storage configuration and enrollment.

Setup checks the initiating user's library readiness before copying programs.
Quit Medal normally, wait for Ready, then choose **Exit Medal Sync** in the
status panel or tray menu first. Older versions call this **Exit status view
(supervision continues)** in the tray menu. Running companion processes,
retained session credentials, incomplete configuration, unavailable coordination
storage and non-CLEAN ownership refuse installation. Setup never force-closes
Medal or automatically reboots. While setup or uninstall is open, new companion
library operations are blocked.

## Programs and personal data

Executables go under the chosen program folder's `bin` directory. Settings,
device identity, logs and backups stay in your user profile. The installed app
reuses an existing `$HOME/.medal-sync` configuration first, then a legacy
`%LOCALAPPDATA%/MedalSync` or `%LOCALAPPDATA%/LarsonServer/MedalSync` configuration. A new setup uses
`$HOME/.medal-sync`; setup does not generate an account, configuration or identity.
Recordings, caches and shared library files remain in their existing locations.

Setup creates its own Start menu and optional desktop shortcuts. It does not
replace Medal's per-user startup, URL handler or legacy portable shortcuts.
The installed companion maintains previously opted-in integration too: when a
Medal update rewrites its URL handler or shortcut, supervision repairs it to
the existing launcher. It does not opt a new user in or migrate that target.
Existing
portable binaries remain available for recovery; use the new Start menu shortcut
for the Program Files build. Repointing existing Medal startup integration is
an explicit advanced migration performed as the owning Windows user.

## Upgrade and repair

Run a newer setup EXE and select the same program folder. It upgrades the
existing installation with the same Installed apps identity and retains data.
Running the same setup again repairs its managed program files. Clearing the
PATH option during an upgrade or repair removes the entry an earlier setup added;
an entry you added yourself is left alone. Older versions
are refused, including when the installed CLI is damaged or missing. Repair uses
the registered version and can restore a damaged CLI. Reuse the registered program
folder; uninstall before moving the programs to another folder.
A destination containing a portable installation or user configuration
is refused so that program and data locations remain separate.

Each desktop launch checks the public GitHub endpoint for a newer stable release
with a two-second network budget. Offline operation, private repositories, rate
limits and missing or malformed releases continue normal launch. When an update
is available, the launch notice points you to **Check for updates**. Nothing
installs until you ask for it.

Choose **Check for updates** in the status panel or tray menu, or **Check for
Medal Sync updates** in the Start menu. It also works before configuration or
enrollment, and reports unavailable checks separately from an up-to-date
version. When a newer stable Windows setup is available, choose **Yes** to
update now:

1. The updater downloads the setup with a native progress window and Cancel
   button. It checks the expected repository URL, download size and GitHub
   SHA-256 before saving a runnable EXE. Failed or canceled downloads remove
   their partial file. Downloads stay under `%LOCALAPPDATA%\MedalSync\Downloads`
   in separate folders; earlier downloads are never overwritten. A download that
   still matches the release is reused instead of downloading again.
2. If Medal is open, a **Medal Sync update** window waits for you to quit Medal
   from its tray icon. Medal Sync never closes Medal or stops supervision for
   you. After Medal quits, the window waits while supervision finishes uploads
   and the library returns to Ready. Choose **Cancel** to install later; the
   verified installer is kept.
3. Once Ready, the updater closes the status app and runs Windows setup
   unattended for the same program folder and install mode, with only its
   progress window. All-users installations still ask for administrator
   approval. Setup repeats its own readiness check and shows any refusal.
4. When setup ends, it reopens the Medal Sync status panel, including after a
   refused or failed installation. Open Medal again when you are ready.

If the library still is not Ready after Medal and supervision stop, for example
because a session needs recovery or another PC owns the library, the update
stops with that guidance and opens the saved installer folder. Fix it, then
choose **Check for updates** again. A program folder that is not a standard
installation location opens the normal setup wizard instead of installing
unattended. Download folders holding only setups for the installed version or
older are removed on the next launch or update check.

To run a downloaded setup manually, quit Medal normally, wait for Ready, and
choose **Exit Medal Sync** in the status panel or tray menu before opening the
EXE. Closing the panel only hides it.

## Uninstall

Open Windows **Settings → Apps → Installed apps → Medal Sync → Uninstall**,
or use **Uninstall Medal Sync** from the Start menu. The native uninstaller
EXE is already bundled in every setup package: installation creates
`unins000.exe` and `unins000.dat` in the chosen program folder. Keep both together;
the EXE uses its installation log to remove only managed files. It can also be
run directly from that folder.
The native uninstaller
requires the programs to be stopped and checks readiness for its Windows user.
It checks again after confirmation, immediately before removing programs.
If you supplied credentials for a different administrator account, quit all
sessions and verify the original user's Ready status before uninstalling.

Uninstall removes managed program files, its own shortcuts, its PATH entry and
Installed apps registration. User settings, identity, recordings, cache, backups and unowned
files are retained. Other accounts' personal state is not modified. The installer
does not support automatically purging user data. Existing portable installations
and their startup integration remain separate.

If setup or uninstall is interrupted, retain all personal state and recovery
receipts, close the interrupted installer and rerun the same or a newer setup
to repair the managed program files before launching Medal Sync. Uninstall does
not provide an application downgrade or revert library generations.

## Package verification

Releases include a SHA-256 sidecar for the setup EXE. Check it against the
trusted release before running the download. The release pipeline also produces
a portable ZIP with an exact file allowlist and per-file SHA-256 manifest.
Public GitHub asset digests support release discovery; requests send no account,
configuration, identity or credentials. Hash verification depends on control of
the repository and HTTPS and does not replace publisher signing. Medal Sync's
own generated binaries and setup EXE are currently unsigned.

## Advanced portable lifecycle tool

The portable ZIP retains `update-medal-sync.ps1` for existing per-user installs.
Use PowerShell 7 and keep the updater, module and startup helper together outside
that installation's bin directory. It supports verified download, preview,
explicit `-Install -Apply`, update `-Apply`, and `-Uninstall -Apply`, with an
explicit `-StateRoot` and trusted package digest. It keeps transactional backups
and receipts under the state root's `updates` directory.
Applying a portable change also blocks native setup and new library operations
for the duration of the transaction. Bare release versions such as `1.3.0.01` and
full tags such as `v1.3.0.01` both resolve to the exact stable release tag.

Use the native setup and Windows uninstaller for Program Files installations;
the portable script refuses a native installation layout.
