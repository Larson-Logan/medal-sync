<#
.SYNOPSIS
Checks GitHub releases, installs, updates or uninstalls Medal Sync.
.DESCRIPTION
Requires PowerShell 7. Check/download needs no live installation. New installs
copy programs only; updates and configured uninstalls require a Ready library.
Use -Install or -Uninstall to select those operations; -Apply performs mutations.
Run a copy outside bin. No operation stops processes or deletes retained data.
#>
[CmdletBinding(DefaultParameterSetName='Online')]
param(
    [Parameter(ParameterSetName='Online')][string]$Tag = 'latest',
    [Parameter(ParameterSetName='Online')][string]$DownloadDirectory,
    [Parameter(Mandatory, ParameterSetName='Local')][string]$PackagePath,
    [Parameter(Mandatory, ParameterSetName='Local')][string]$ExpectedSha256,
    [Parameter(ParameterSetName='Local')][switch]$Install,
    [Parameter(Mandatory, ParameterSetName='Uninstall')][switch]$Uninstall,
    [Parameter(ParameterSetName='Local')][Parameter(ParameterSetName='Uninstall')][switch]$Apply,
    [Parameter(ParameterSetName='Local')][Parameter(Mandatory, ParameterSetName='Uninstall')][string]$StateRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MedalSync.Update.psm1') -Force
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('medal-sync-update-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$mutex = $null
$locked = $false
try {
    if ($Apply) {
        if (-not $IsWindows) { throw 'Installation is supported on Windows only.' }
        $root = Assert-MedalLocalPath $StateRoot
        if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ieq (Join-Path $root 'bin')) { throw 'Run a copy of the updater, module and startup helper from outside bin.' }
        $mutex = [Threading.Mutex]::new($false, 'Local\LarsonServer.MedalSync')
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'A Medal Sync session or another installer is running.' }
    }
    if ($Uninstall) {
        if (-not $Apply) { Write-Output 'Preview: removes companion programs and restores saved startup entries. Retains configuration, identity, backups, cache, media and dependencies. Add -Apply to uninstall.'; return }
        $receipt = Uninstall-MedalRelease $root
        Write-Output "Uninstalled companion programs. Recovery receipt: $receipt"
        return
    }
    if ($PSCmdlet.ParameterSetName -eq 'Online') {
        $release = Get-MedalRelease $Tag
        $release | Format-List
        if (-not $DownloadDirectory) { return }
        $PackagePath = Join-Path $scratch $release.Name
        Invoke-WebRequest -Uri $release.Url -OutFile $PackagePath -TimeoutSec 300
        if ((Get-Item -LiteralPath $PackagePath).Length -ne $release.Size) { throw 'Downloaded archive length mismatch.' }
        $ExpectedSha256 = $release.Sha256
    }
    $staged = Join-Path $scratch 'payload'
    $manifest = Read-MedalReleasePackage $PackagePath $ExpectedSha256 $staged
    if ($PSCmdlet.ParameterSetName -eq 'Online') {
        if ($manifest.version -cne $release.Version) { throw 'Package version does not match release tag.' }
        $null = New-Item -ItemType Directory -Path $DownloadDirectory -Force
        $destination = Join-Path $DownloadDirectory $release.Name
        if (Test-Path -LiteralPath $destination) { throw 'Download destination already exists.' }
        Copy-Item -LiteralPath $PackagePath -Destination $destination
        Write-Output "Verified package: $destination"
    } elseif ($Apply) {
        if (-not $IsWindows) { throw 'Installation is supported on Windows only.' }
        if ($Install) { $receipt = Install-MedalFresh $root $staged $manifest }
        else { $null = Assert-MedalInstallPath $root; $receipt = Install-MedalRelease $root $staged $manifest }
        Write-Output "Installed $($manifest.version). Recovery receipt: $receipt"
    } else {
        Write-Output "Verified Medal Sync $($manifest.version) for $($manifest.runtime). Preview only; use -Apply -StateRoot ABSOLUTE_PATH to install."
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    # This exact directory was created above with a random name in the temp root.
    if ([IO.Path]::GetDirectoryName($scratch) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) { throw 'Unexpected scratch path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
