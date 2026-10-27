# Installation, updates and removal

Use PowerShell 7 on Windows x64. The same `update-medal-sync.ps1` handles release
discovery, download, installation, update and uninstall. It never kills Medal,
changes library ownership or deletes recordings. Run a copy outside the installed
`bin` directory so the tool can replace or remove its own installed files.

## Automatic launch checks

Each desktop launch, including sign-in and opening an existing session, requests
the latest stable release from the public GitHub API. The entire request and
response have a two-second cancellation budget and a one-megabyte response limit.
A newer release with the expected Windows package and digest produces a notice.
The request contains no account, configuration, device identity or credentials.

Private repositories, unpublished releases, rate limits, offline operation and
malformed responses silently continue normal launch. Once the repository is
public and a stable release is published, discovery starts working without
changing configuration. Checks do not install automatically.

## Check and download

From a source checkout (or a trusted copy of the release's scripts):

```powershell
$updater = './scripts/storage/medal-sync/update-medal-sync.ps1'
pwsh -File $updater
pwsh -File $updater -DownloadDirectory "$HOME/Downloads/MedalSync"
# Select a specific stable release: add -Tag v0.2.0.
```

The downloader uses only this project's public GitHub release endpoint. It
requires GitHub's SHA-256 asset digest, checks the archive length and hash, then
validates its manifest and every payload hash before retaining the download.
No authentication token is used. A private repository or absent release fails
with a visible error when you explicitly invoke the tool.

When downloading manually, obtain the digest from the release's asset details
or `.sha256` file through a trusted channel. A hash protects against corruption;
it does not provide an independent publisher signature. Binaries are unsigned.

## Install or update

Extract the release into a temporary tools directory. Keep the updater, module
and startup helper together. The archive remains the installation input:

```powershell
$package = "$HOME/Downloads/MedalSync/medal-sync-0.2.0-win-x64.zip"
$hash = 'REPLACE_WITH_RELEASE_SHA256'
$root = "$HOME/.medal-sync"
# Run from the extracted tools directory:
pwsh -File ./update-medal-sync.ps1 -PackagePath $package -ExpectedSha256 $hash
# New installation (programs only):
pwsh -File ./update-medal-sync.ps1 -PackagePath $package -ExpectedSha256 $hash -Install -Apply -StateRoot $root
# Update an existing, configured installation:
pwsh -File ./update-medal-sync.ps1 -PackagePath $package -ExpectedSha256 $hash -Apply -StateRoot $root
```

Without `-Apply`, a local package is only verified. New installation requires a
new or empty bin directory; follow [setup](getting-started.md) to configure it.
Reinstallation after a completed uninstall also uses `-Install`; it preserves
retained configuration/identity, refuses downgrades and verifies Ready status
using the newly copied CLI before completing. Failed verification removes the
new programs and retains the state. Updates require both existing programs, valid adjacent state, no companion or
Medal process, a reachable **CLEAN** journal with no owner/session, and no retained
local session login files. The version must increase. Downgrades and reinstalling
the same installed version through update are refused.

All mutations hold the same Windows session mutex as launch/finish. Paths must
be explicit, local and free of symbolic links or junctions. Only the named
companion payload files change; configuration, identity, rclone, cache, media,
databases and recovery backups remain in place. The package includes licenses.

## Uninstall

Use a copy of all three scripts outside the installation:

```powershell
pwsh -File ./update-medal-sync.ps1 -Uninstall -StateRoot "$HOME/.medal-sync"
pwsh -File ./update-medal-sync.ps1 -Uninstall -Apply -StateRoot "$HOME/.medal-sync"
```

The first command previews removal. Apply requires Medal stopped and, for a
configured installation, verified Ready status. Saved startup entries and
shortcuts are restored using the startup helper. Remaining references to this
installation block binary removal. The companion payload is backed up and
removed; configuration, identity, receipts, cache, media, databases and dependency
programs are retained. There is no data-purge option.

## Rollback and interrupted operations

Update and uninstall retain original payload files under
`STATE_ROOT/updates/TRANSACTION/previous/`, with `receipt.json` describing the
operation. Copy, verification or smoke-test failure restores previous payloads
and records `rolledBack`. A successful operation records `complete`.

Power loss is not an atomic multi-file update. Any unfinished receipt blocks
another operation. Quit Medal, keep the installation offline, inspect that
receipt and restore each previous payload to its exact `bin` name. For a failed
fresh install, remove only the payload names listed by `Get-MedalPayloadNames`;
there was no previous payload. Verify both executable versions before marking
the receipt `rolledBack`. Preserve all evidence and state files.

To undo a completed uninstall, use `-Install` with the same or a newer verified
package, or restore its previous payload files to `bin` and verify both versions.
Startup
restoration is retained after a binary rollback; preview and reapply startup
integration separately if needed. A normal relaunch performs the usual library
admission checks.
