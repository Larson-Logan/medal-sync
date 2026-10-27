<# .SYNOPSIS
Tests real rclone copies, named remotes, cancellation and sequential two-PC WinFsp handoff using disposable synthetic data.
#>
[CmdletBinding()]
param([switch]$SkipMount)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Storage integration runs on Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$dependencies = & (Join-Path $PSScriptRoot 'setup-storage-toolchain.ps1')
$driverPresent = Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'WinFsp/bin/winfsp-x64.dll')
if (-not $SkipMount -and -not $driverPresent) {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'Real mount testing requires an existing WinFsp installation. This test never installs drivers on a personal PC.'
    }
    $driver = Start-Process -FilePath msiexec.exe -ArgumentList @('/i',('"{0}"' -f (Join-Path $dependencies 'winfsp.msi')),'/quiet','/norestart') -WindowStyle Hidden -Wait -PassThru
    if ($driver.ExitCode -notin @(0,3010)) { throw 'WinFsp installation failed on the ephemeral runner.' }
}
& dotnet restore (Join-Path $root 'tests/integration/MedalSync.Integration.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Integration restore failed.' }
$arguments = @('run','--project',(Join-Path $root 'tests/integration/MedalSync.Integration.csproj'),'-c','Release','--no-restore','--','--rclone',(Join-Path $dependencies 'rclone.exe'))
if (-not $SkipMount) { $arguments += '--mount' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Real-process storage integration failed.' }
