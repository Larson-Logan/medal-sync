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
$group = 'Medal Sync'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$group/Medal Sync.lnk"
if (Test-Path -LiteralPath (Split-Path -Parent $shortcut)) { throw 'An existing shortcut group is present; refusing an integration test.' }
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="{0}"' -f $app),('/LOG="{0}"' -f (Join-Path $scratch 'setup.log')))
function Run-Setup([string[]]$Extra = @()) {
    $process = Start-Process -FilePath $setup -ArgumentList ($arguments + $Extra) -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
# Per-user setup edits the user PATH, which is disposable on this runner.
$bin = [IO.Path]::GetFullPath((Join-Path $app 'bin'))
function Get-UserPathEntries {
    $value = (Get-Item -LiteralPath 'HKCU:\Environment').GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    return @($value -split ';' | Where-Object { $_.Trim() })
}
function Get-BinPathCount {
    return @(Get-UserPathEntries | Where-Object {
        try { [IO.Path]::GetFullPath($_.Trim().Trim('"')).TrimEnd('\') -ieq $bin.TrimEnd('\') } catch { $false }
    }).Count
}
function Set-UserPathEntries([string[]]$Entries) {
    $environment = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        if (-not $Entries) { $environment.DeleteValue('Path', $false) }
        else { $environment.SetValue('Path', ($Entries -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString) }
    } finally { $environment.Dispose() }
}
$originalPath = Get-UserPathEntries
if ((Get-BinPathCount) -ne 0) { throw 'The test program folder is already on PATH.' }
if ((Run-Setup @('/MERGETASKS="addtopath"')) -ne 0) { throw 'Native fresh install failed. Inspect the setup log.' }
if ((Get-BinPathCount) -ne 1) { throw 'Setup did not add the program folder to PATH once.' }
$savedPath = $env:Path
try {
    $env:Path = (Get-UserPathEntries) -join ';'
    $resolved = (Get-Command 'medal-sync' -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
} finally { $env:Path = $savedPath }
if ([IO.Path]::GetFullPath($resolved) -ine (Join-Path $bin 'medal-sync.exe')) { throw "medal-sync resolved to $resolved instead of the installed CLI." }
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
if (-not (Test-Path -LiteralPath (Join-Path $app 'bin/rclone.exe')) -or -not (Test-Path -LiteralPath (Join-Path $app 'RCLONE-LICENSE.txt'))) { throw 'Native setup did not install bundled storage dependencies and notices.' }
$sentinel = Join-Path $app 'unowned-user-file.txt'
'preserve me' | Set-Content -LiteralPath $sentinel
if ((Run-Setup) -ne 0) { throw 'Native in-place upgrade/repair failed.' }
if ((Get-BinPathCount) -ne 1) { throw 'Repair did not keep exactly one PATH entry.' }
if ((Run-Setup @('/MERGETASKS="!addtopath"')) -ne 0) { throw 'Repair without the PATH option failed.' }
if ((Get-BinPathCount) -ne 0) { throw 'Clearing the PATH option did not remove its entry.' }
# An entry the user added by hand stays theirs, even after selecting and clearing the option.
Set-UserPathEntries (@(Get-UserPathEntries) + $bin)
if ((Run-Setup @('/MERGETASKS="addtopath"')) -ne 0) { throw 'Repair with a user PATH entry failed.' }
if ((Get-BinPathCount) -ne 1) { throw 'Setup duplicated a user PATH entry.' }
if ((Run-Setup @('/MERGETASKS="!addtopath"')) -ne 0) { throw 'Repair clearing the PATH option over a user entry failed.' }
if ((Get-BinPathCount) -ne 1) { throw 'Clearing the PATH option removed an entry the user added.' }
Set-UserPathEntries $originalPath
if ((Run-Setup @('/MERGETASKS="addtopath"')) -ne 0) { throw 'Repair with the PATH option failed.' }
if ((Get-BinPathCount) -ne 1) { throw 'Selecting the PATH option again did not restore its entry.' }
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
if ((Get-BinPathCount) -ne 0) { throw 'Uninstall left the program folder on PATH.' }
if (($originalPath -join ';') -cne ((Get-UserPathEntries) -join ';')) { throw 'Setup changed other PATH entries.' }
Write-Output 'PASS: native installation, registration, shortcuts, PATH option, in-place upgrade, busy refusal and safe uninstall'
