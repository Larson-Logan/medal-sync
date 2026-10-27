<#
.SYNOPSIS
Points Medal's startup entries at medal-sync-launch.exe, or puts the originals back.

.DESCRIPTION
Preview by default: lists each entry and what -Apply would do, and writes nothing.

-Apply   Saves the originals once (startup-originals.json, plus copies of the
         shortcut files), then removes Run values that start Medal.exe directly,
         adds the 'MedalSync' Run value, and points the medal:// handler and
         Medal's shortcuts at the launcher, keeping Medal's icon. Running it
         again after a Medal update repoints whatever the update restored and
         keeps the first saved originals.
-Restore Puts the saved originals back. The launcher stops guarding the
         entries once the originals file is retired.
-Check   Reports each entry; exits 1 when one still starts Medal directly.

Per-user only: HKCU and the user's shortcut folders. No administrator rights.
See docs/medal-sync.md.
#>
[CmdletBinding(DefaultParameterSetName = 'Preview')]
param(
    [Parameter(ParameterSetName = 'Apply')][switch]$Apply,
    [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
    [Parameter(ParameterSetName = 'Check')][switch]$Check,
    [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'LarsonServer\MedalSync'),
    [string]$Launcher,
    # Tests point these at a scratch registry key standing in for HKCU:\Software
    # and at scratch shortcut folders.
    [string]$SoftwareKey = 'HKCU:\Software',
    [string[]]$ShortcutFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Medal startup entries exist only on Windows.' }
if (-not $SoftwareKey.StartsWith('HKCU:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only per-user (HKCU) registry keys are changed.' }

if (-not $Launcher) { $Launcher = Join-Path $StateRoot 'bin\medal-sync-launch.exe' }
$customShortcutFolders = [bool]$ShortcutFolder
if (-not $ShortcutFolder) {
    $ShortcutFolder = @(
        [Environment]::GetFolderPath('Startup'),
        [Environment]::GetFolderPath('Programs'),
        [Environment]::GetFolderPath('DesktopDirectory'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
}
$libraryShortcutFolders = if ($customShortcutFolders) { $ShortcutFolder } else {
    @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'))
}
$libraryShortcuts = @($libraryShortcutFolders | Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    ForEach-Object { Join-Path $_ 'Medal Library.lnk' } | Select-Object -Unique)
$libraryOriginalsPath = Join-Path $StateRoot 'library-shortcuts.json'
$runKey = Join-Path $SoftwareKey 'Microsoft\Windows\CurrentVersion\Run'
$protocolKey = Join-Path $SoftwareKey 'Classes\medal\shell\open\command'
$originalsPath = Join-Path $StateRoot 'startup-originals.json'
$launcherName = 'medal-sync-launch.exe'
$runValueName = 'MedalSync'
$runCommand = '"{0}" --at-sign-in' -f $Launcher
$protocolCommand = '"{0}" "%1"' -f $Launcher
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$shell = New-Object -ComObject WScript.Shell

function Test-Launcher([string]$Command) {
    [bool]$Command -and $Command.IndexOf($launcherName, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-Direct([string]$Command) {
    [bool]$Command -and -not (Test-Launcher $Command) -and $Command.IndexOf('\Medal.exe', [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-RunValue {
    if (-not (Test-Path -LiteralPath $runKey)) { return }
    $key = Get-Item -LiteralPath $runKey
    foreach ($name in $key.GetValueNames()) {
        $value = $key.GetValue($name)
        if ($value -is [string]) { [pscustomobject]@{ Name = $name; Command = $value } }
    }
}

function Get-Protocol {
    if (-not (Test-Path -LiteralPath $protocolKey)) { return $null }
    (Get-Item -LiteralPath $protocolKey).GetValue('')
}

function Get-Shortcut {
    foreach ($folder in $ShortcutFolder) {
        if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*Medal*.lnk' -File) {
            $link = $shell.CreateShortcut($file.FullName)
            [pscustomobject]@{ Path = $file.FullName; Target = $link.TargetPath; IconLocation = $link.IconLocation }
        }
    }
}

function Set-ShortcutTarget([string]$Path, [string]$Target, [string]$Icon) {
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = ''
    $link.WorkingDirectory = Split-Path -Parent $Target
    if ($Icon) { $link.IconLocation = $Icon }
    $link.Save()
}

$runValues = @(Get-RunValue)
$directRun = @($runValues | Where-Object { Test-Direct $_.Command })
$ours = @($runValues | Where-Object { $_.Name -eq $runValueName })
$protocol = Get-Protocol
$shortcuts = @(Get-Shortcut)
$directShortcuts = @($shortcuts | Where-Object { Test-Direct $_.Target })

function Write-Report {
    foreach ($value in $runValues) {
        $state = if (Test-Launcher $value.Command) { 'via medal-sync' } elseif (Test-Direct $value.Command) { 'STARTS MEDAL DIRECTLY' } else { 'not Medal' }
        if ($state -ne 'not Medal') { Write-Host "Run value '$($value.Name)': $state" }
    }
    if ($ours.Count -eq 0) { Write-Host "Run value '$runValueName': missing" }
    $protocolState = if ($null -eq $protocol) { 'not registered' } elseif (Test-Launcher $protocol) { 'via medal-sync' } elseif (Test-Direct $protocol) { 'STARTS MEDAL DIRECTLY' } else { $protocol }
    Write-Host "medal:// handler: $protocolState"
    foreach ($shortcut in $shortcuts) {
        $state = if (Test-Launcher $shortcut.Target) { 'via medal-sync' } elseif (Test-Direct $shortcut.Target) { 'STARTS MEDAL DIRECTLY' } else { "points at $($shortcut.Target)" }
        Write-Host "Shortcut $($shortcut.Path): $state"
    }
}

if ($Check) {
    Write-Report
    $drift = $directRun.Count + $directShortcuts.Count + [int](Test-Direct $protocol) + [int]($ours.Count -eq 0)
    foreach ($path in $libraryShortcuts) {
        if (-not (Test-Path -LiteralPath $path) -or -not (Test-Launcher ($shell.CreateShortcut($path).TargetPath))) {
            Write-Host "Stable Medal Library shortcut is missing or changed: $path"
            $drift++
        }
    }
    if ($drift -gt 0) { Write-Host "$drift entr$(if ($drift -eq 1) { 'y' } else { 'ies' }) bypass the launcher; run with -Apply."; exit 1 }
    Write-Host 'Every Medal startup entry goes through medal-sync.'
    exit 0
}

if ($Restore) {
    if (-not (Test-Path -LiteralPath $originalsPath)) { throw "No saved originals at $originalsPath; nothing to restore." }
    $saved = Get-Content -LiteralPath $originalsPath -Raw | ConvertFrom-Json
    if ($saved.format -ne 1) { throw "Unsupported originals format in $originalsPath." }
    foreach ($value in $runValues | Where-Object { Test-Launcher $_.Command }) {
        Remove-ItemProperty -LiteralPath $runKey -Name $value.Name
    }
    foreach ($value in @($saved.runValues)) {
        New-ItemProperty -LiteralPath $runKey -Name $value.name -Value $value.command -PropertyType String -Force | Out-Null
    }
    if ($null -ne $saved.protocol -and (Test-Path -LiteralPath $protocolKey)) { Set-Item -LiteralPath $protocolKey -Value $saved.protocol }
    foreach ($shortcut in @($saved.shortcuts)) { Copy-Item -LiteralPath $shortcut.backup -Destination $shortcut.path -Force }
    if (Test-Path -LiteralPath $libraryOriginalsPath) {
        $librarySaved = Get-Content -Raw -LiteralPath $libraryOriginalsPath | ConvertFrom-Json
        if ($librarySaved.format -ne 1) { throw 'Unsupported stable-shortcut receipt.' }
        foreach ($path in @($librarySaved.created)) {
            # Remove only this task's exact created shortcut in a selected folder,
            # and only while it still targets the managed launcher.
            if ($path -notin $libraryShortcuts) { throw "Stable shortcut receipt points outside the selected folders: $path" }
            if ((Test-Path -LiteralPath $path) -and (Test-Launcher ($shell.CreateShortcut($path).TargetPath))) {
                Remove-Item -LiteralPath $path
            }
        }
        Move-Item -LiteralPath $libraryOriginalsPath -Destination "$libraryOriginalsPath.restored-$stamp"
    }
    # Shortcuts a Medal update created after -Apply, then repointed: back to Medal.
    foreach ($shortcut in @(Get-Shortcut | Where-Object { Test-Launcher $_.Target })) {
        Set-ShortcutTarget $shortcut.Path $saved.medalExecutable $null
    }
    $retired = "$originalsPath.restored-$stamp"
    Move-Item -LiteralPath $originalsPath -Destination $retired
    Write-Host "Restored Medal's original startup entries. The saved originals are kept as $retired."
    exit 0
}

$medalExecutable = @($directShortcuts | ForEach-Object { $_.Target }) + @($directRun | ForEach-Object { ($_.Command -split '"' | Where-Object { Test-Direct $_ } | Select-Object -First 1) }) |
    Where-Object { $_ } | Select-Object -First 1
if (-not $medalExecutable) { $medalExecutable = Join-Path $env:LOCALAPPDATA 'Medal\current\Medal.exe' }

if (-not $Apply) {
    Write-Report
    foreach ($value in $directRun) { Write-Host "-Apply removes Run value '$($value.Name)' (saved first)." }
    if ($ours.Count -eq 0) { Write-Host "-Apply adds Run value '$runValueName' = $runCommand" }
    if (Test-Direct $protocol) { Write-Host "-Apply points the medal:// handler at the launcher: $protocolCommand" }
    foreach ($shortcut in $directShortcuts) { Write-Host "-Apply points $($shortcut.Path) at the launcher, keeping Medal's icon." }
    foreach ($path in $libraryShortcuts) { Write-Host "-Apply provides a stable Medal Library shortcut: $path" }
    Write-Host 'Preview only; nothing was changed.'
    exit 0
}

if (-not (Test-Path -LiteralPath $Launcher)) { throw "The launcher is not installed at $Launcher; run publish-medal-sync.ps1 -Install first." }
foreach ($path in $libraryShortcuts) {
    if (Test-Path -LiteralPath $path) {
        $target = $shell.CreateShortcut($path).TargetPath
        if (-not (Test-Launcher $target) -and -not (Test-Direct $target)) {
            throw "An unrelated shortcut already occupies $path; it was not overwritten."
        }
    }
}
if (-not (Test-Path -LiteralPath $originalsPath)) {
    $backup = Join-Path $StateRoot "backups\startup-$stamp"
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $index = 0
    $savedShortcuts = foreach ($shortcut in $directShortcuts) {
        $copy = Join-Path $backup ('{0}-{1}' -f ++$index, (Split-Path -Leaf $shortcut.Path))
        Copy-Item -LiteralPath $shortcut.Path -Destination $copy
        [ordered]@{ path = $shortcut.Path; backup = $copy }
    }
    $originals = [ordered]@{
        format = 1
        savedAt = (Get-Date).ToUniversalTime().ToString('o')
        medalExecutable = $medalExecutable
        runValues = @($directRun | ForEach-Object { [ordered]@{ name = $_.Name; command = $_.Command } })
        protocol = $protocol
        shortcuts = @($savedShortcuts)
    }
    New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
    $originals | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $originalsPath -Encoding utf8NoBOM
    Write-Host "Saved the originals to $originalsPath and the shortcut files to $backup."
}

if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
foreach ($value in $directRun) { Remove-ItemProperty -LiteralPath $runKey -Name $value.Name }
New-ItemProperty -LiteralPath $runKey -Name $runValueName -Value $runCommand -PropertyType String -Force | Out-Null
if (Test-Direct $protocol) { Set-Item -LiteralPath $protocolKey -Value $protocolCommand }
foreach ($shortcut in $directShortcuts) {
    $icon = if ($shortcut.IconLocation -and $shortcut.IconLocation -notmatch '^,?-?\d*$') { $shortcut.IconLocation } else { "$($shortcut.Target),0" }
    Set-ShortcutTarget $shortcut.Path $Launcher $icon
}
$created = @()
if (Test-Path -LiteralPath $libraryOriginalsPath) {
    $librarySaved = Get-Content -Raw -LiteralPath $libraryOriginalsPath | ConvertFrom-Json
    if ($librarySaved.format -ne 1) { throw 'Unsupported stable-shortcut receipt.' }
    $created = @($librarySaved.created)
}
foreach ($path in $libraryShortcuts) {
    if (-not (Test-Path -LiteralPath $path)) { $created += $path }
}
[ordered]@{ format=1; created=@($created | Select-Object -Unique) } | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath $libraryOriginalsPath -Encoding utf8NoBOM
foreach ($path in $libraryShortcuts) { Set-ShortcutTarget $path $Launcher "$medalExecutable,0" }
Write-Host "Medal's startup entries now go through $Launcher."
Write-Host 'Use Medal Library for a shortcut independent of the vendor-owned Medal.lnk.'
