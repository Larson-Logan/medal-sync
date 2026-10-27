<# .SYNOPSIS
Builds the standard Windows setup EXE from the verified release ZIP.
#>
[CmdletBinding()]
param([switch]$SkipPackage)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows setup is built on Windows only.' }
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.VersionPrefix
if (-not $SkipPackage) {
    & (Join-Path $PSScriptRoot 'package-release.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Release packaging failed.' }
}
$compiler = & (Join-Path $PSScriptRoot 'setup-installer-toolchain.ps1')
$storageDependencies = & (Join-Path $PSScriptRoot 'setup-storage-toolchain.ps1')
Import-Module (Join-Path $PSScriptRoot 'storage/medal-sync/MedalSync.Update.psm1') -Force
$output = Join-Path $root '.medal-sync-dist'
$archive = Join-Path $output "medal-sync-$version-win-x64.zip"
$hash = ((Get-Content -LiteralPath "$archive.sha256" -Raw) -split '\s+')[0]
$payload = Join-Path $output ('setup-payload-' + [guid]::NewGuid().ToString('N'))
try {
    $manifest = Read-MedalReleasePackage $archive $hash $payload
    if ($manifest.version -cne $version) { throw 'Setup and payload versions differ.' }
    $arguments = @('/Qp',('/DAppVersion={0}' -f $version),('/DPayloadDir={0}' -f $payload),('/DStorageDependencies={0}' -f $storageDependencies),('/DOutputPath={0}' -f $output),(Join-Path $root 'installer/windows/medal-sync.iss'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Windows setup compilation failed.' }
    $setup = Join-Path $output "medal-sync-$version-setup-win-x64.exe"
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
    Write-Output "Built Windows installer: $setup"
    Write-Output "SHA-256 $setupHash"
} finally {
    if ([IO.Path]::GetDirectoryName($payload) -ne $output) { throw 'Unexpected setup staging path.' }
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
}
