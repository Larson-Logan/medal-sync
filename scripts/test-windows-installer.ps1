<# .SYNOPSIS
Exercises native setup, upgrade, busy refusal and uninstall on an ephemeral CI desktop.
.DESCRIPTION
This integration test writes a temporary per-user Installed apps entry and
shortcut group. It runs only on GitHub-hosted Windows runners, never a live PC.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Native installer integration requires an ephemeral GitHub-hosted Windows runner.'
}
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.VersionPrefix
$setup = Join-Path $root ".medal-sync-dist/medal-sync-$version-setup-win-x64.exe"
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{168562DF-1041-441D-96D0-2244EA744E18}_is1'
$machineKey = $key.Replace('HKCU:', 'HKLM:')
if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $machineKey)) { throw 'An existing installation is present; refusing an integration test.' }
$scratch = Join-Path $root ('.validation/setup-smoke-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $scratch 'Program Files/Medal Sync'
$group = 'Medal Sync Integration ' + [guid]::NewGuid().ToString('N')
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$group/Medal Sync.lnk"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="{0}"' -f $app),('/GROUP="{0}"' -f $group),('/LOG="{0}"' -f (Join-Path $scratch 'setup.log')))
function Run-Setup {
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
if ((Run-Setup) -ne 0) { throw 'Native fresh install failed. Inspect the setup log.' }
if (-not (Test-Path -LiteralPath $key) -or -not (Test-Path -LiteralPath $shortcut)) {
    Write-Output "Expected registration: $key; exists=$(Test-Path -LiteralPath $key)"
    Write-Output "Expected shortcut: $shortcut; exists=$(Test-Path -LiteralPath $shortcut)"
    foreach ($hive in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        Get-ChildItem -LiteralPath $hive -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like '*Medal*' } | Select-Object PSChildName,DisplayName,InstallLocation | Format-List
    }
    Get-Content -LiteralPath (Join-Path $scratch 'setup.log') -Tail 100
    throw 'Installed apps registration or Start menu shortcut is missing.'
}
foreach ($name in @('medal-sync.exe','medal-sync-launch.exe')) {
    if ((Get-FileHash -LiteralPath (Join-Path $app "bin/$name")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root ".medal-sync-dist/$name")).Hash) { throw 'Installed executable hash differs from release.' }
}
$sentinel = Join-Path $app 'unowned-user-file.txt'
'preserve me' | Set-Content -LiteralPath $sentinel
if ((Run-Setup) -ne 0) { throw 'Native in-place upgrade/repair failed.' }
$mutex = [Threading.Mutex]::new($false, 'Local\LarsonServer.MedalSync')
try { if ((Run-Setup) -eq 0) { throw 'Setup did not refuse an active session mutex.' } } finally { $mutex.Dispose() }
$cli = Join-Path $app 'bin/medal-sync.exe'
& $cli version
if ($LASTEXITCODE -ne 0) { throw 'Installed CLI smoke check failed.' }
$setupMutex = [Threading.Mutex]::new($false, 'Global\LarsonServer.MedalSync.Setup')
try {
    $busyUninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($busyUninstall.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $cli)) { throw 'Uninstall did not refuse concurrent setup.' }
} finally { $setupMutex.Dispose() }
$uninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw 'Native uninstall failed.' }
if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $cli)) { throw 'Uninstall left managed registration, shortcut or executable behind.' }
if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve me') { throw 'Uninstall removed unowned user files.' }
Write-Output 'PASS: native installation, registration, shortcuts, in-place upgrade, busy refusal and safe uninstall'
