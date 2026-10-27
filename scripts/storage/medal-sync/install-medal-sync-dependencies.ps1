<#
.SYNOPSIS
Installs the pinned rclone build for medal-sync and checks WinFsp. Preview by default.

.DESCRIPTION
Reads tools/medal-sync/config/dependencies.json. With -Apply it downloads the
pinned rclone zip into a new empty folder, refuses it unless the SHA-256
matches, and copies only rclone.exe into %LOCALAPPDATA%\LarsonServer\MedalSync\bin.
WinFsp needs administrator approval, so this script never installs it: it
reports whether it is present and prints the pinned MSI and digest for the
owner to install. Nothing here changes Medal, its settings or any clip.
#>
[CmdletBinding()]
param([switch]$Apply)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'medal-sync runs on Windows only.' }

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$pins = Get-Content -Raw -LiteralPath (Join-Path $root 'tools\medal-sync\config\dependencies.json') | ConvertFrom-Json
$binDirectory = Join-Path $env:LOCALAPPDATA 'LarsonServer\MedalSync\bin'
$rclone = Join-Path $binDirectory 'rclone.exe'

function Get-WinFspDirectory {
    $key = 'HKLM:\SOFTWARE\WOW6432Node\WinFsp'
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $directory = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).InstallDir
    if ($directory -and (Test-Path -LiteralPath (Join-Path $directory 'bin\winfsp-x64.dll'))) { return $directory }
    return $null
}

$winfsp = Get-WinFspDirectory
if ($winfsp) {
    Write-Host "WinFsp: installed at $winfsp"
} else {
    Write-Host 'WinFsp: NOT installed. The owner installs it once (administrator approval):'
    Write-Host "  $($pins.winfsp.url)"
    Write-Host "  SHA-256 $($pins.winfsp.sha256) ($($pins.winfsp.bytes) bytes). Check with Get-FileHash before running it."
}

if (Test-Path -LiteralPath $rclone) {
    $installedHash = (Get-FileHash -LiteralPath $rclone -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "rclone: present at $rclone (SHA-256 of the exe: $installedHash)"
    if ($installedHash -eq $pins.rclone.exeSha256) { Write-Host 'Pinned rclone already installed; no download or replacement needed.'; return }
    if (-not $Apply) { return }
    if (Get-Process -Name rclone -ErrorAction SilentlyContinue) { throw 'Close the Medal session and wait for Ready before replacing rclone.' }
}

if (-not $Apply) {
    Write-Host "Preview: with -Apply, rclone $($pins.rclone.version) is downloaded from $($pins.rclone.url),"
    Write-Host "checked against SHA-256 $($pins.rclone.sha256), and rclone.exe is copied to $binDirectory."
    return
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('medal-sync-rclone-' + [guid]::NewGuid().ToString('N'))
if (-not [IO.Path]::GetFullPath($work).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary path escaped the expected directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work $pins.rclone.asset
    Invoke-WebRequest -Uri $pins.rclone.url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pins.rclone.sha256) {
        throw "Downloaded rclone does not match the pinned SHA-256 (got $actual). Nothing was installed."
    }
    $extracted = Join-Path $work 'extracted'
    Expand-Archive -LiteralPath $zip -DestinationPath $extracted
    $candidates = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter 'rclone.exe')
    if ($candidates.Count -ne 1) { throw "Expected one rclone.exe in the archive, found $($candidates.Count)." }
    if ((Get-FileHash -LiteralPath $candidates[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pins.rclone.exeSha256) { throw 'Extracted rclone.exe does not match the reviewed executable digest.' }
    New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $rclone) {
        $backup = Join-Path $binDirectory ('rclone.exe.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.previous')
        Move-Item -LiteralPath $rclone -Destination $backup
        Write-Host "Previous rclone kept as $backup"
    }
    Copy-Item -LiteralPath $candidates[0].FullName -Destination $rclone
    Write-Host "Installed rclone $($pins.rclone.version) at $rclone"
    Write-Host "Archive SHA-256 $actual; exe SHA-256 $((Get-FileHash -LiteralPath $rclone -Algorithm SHA256).Hash.ToLowerInvariant())"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
