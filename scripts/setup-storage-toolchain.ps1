<# .SYNOPSIS
Stages pinned storage dependencies for packaging and isolated integration tests. Installs nothing.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pins = Get-Content -LiteralPath (Join-Path $root 'tools/medal-sync/config/dependencies.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root '.validation/build-tools/storage'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
function Get-VerifiedDownload($Pin) {
    $target = Join-Path $cache $Pin.asset
    if (-not (Test-Path -LiteralPath $target)) {
        $partial = $target + '.' + [guid]::NewGuid().ToString('N') + '.partial'
        try {
            Invoke-WebRequest -Uri $Pin.url -OutFile $partial -TimeoutSec 180
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $Pin.sha256) { throw 'Storage dependency download SHA-256 mismatch.' }
            Move-Item -LiteralPath $partial -Destination $target
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $Pin.sha256) { throw "Cached dependency digest differs from its pin: $($Pin.asset)." }
    return $target
}
$archive = Get-VerifiedDownload $pins.rclone
$msi = Get-VerifiedDownload $pins.winfsp
$extract = Join-Path $cache ('extract-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $binaries = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'rclone.exe')
    $licenses = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'README.txt')
    if ($binaries.Count -ne 1 -or $licenses.Count -ne 1) { throw 'Expected one pinned rclone executable and license.' }
    if ((Get-FileHash -LiteralPath $binaries[0].FullName -Algorithm SHA256).Hash -ine $pins.rclone.exeSha256) { throw 'Extracted rclone digest differs from the executable pin.' }
    $stagedRclone = Join-Path $cache 'rclone.exe'
    if (-not (Test-Path -LiteralPath $stagedRclone) -or (Get-FileHash -LiteralPath $stagedRclone -Algorithm SHA256).Hash -ine $pins.rclone.exeSha256) {
        Copy-Item -LiteralPath $binaries[0].FullName -Destination $stagedRclone -Force
    }
    # The vendor ZIP puts the complete MIT license and copyright in README.txt.
    $notice = Get-Content -LiteralPath $licenses[0].FullName -Raw
    if ($notice -notmatch 'Permission is hereby granted' -or $notice -notmatch 'THE SOFTWARE IS PROVIDED') { throw 'Pinned rclone notices lack the expected license text.' }
    Copy-Item -LiteralPath $licenses[0].FullName -Destination (Join-Path $cache 'RCLONE-LICENSE.txt') -Force
    Copy-Item -LiteralPath $msi -Destination (Join-Path $cache 'winfsp.msi') -Force
} finally {
    $expectedParent = [IO.Path]::GetFullPath($cache)
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($extract)) -ne $expectedParent) { throw 'Unexpected dependency staging directory.' }
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
}
Write-Output $cache
