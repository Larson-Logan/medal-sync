<#
.SYNOPSIS
Builds the self-contained medal-sync.exe and medal-sync-launch.exe and records their SHA-256.

.DESCRIPTION
Publishes tools/medal-sync with locked package versions into the ignored
.medal-sync-dist folder: medal-sync.exe (the command line) and
medal-sync-launch.exe (the same program without a console window, for the
rewired startup entries). With -Install it copies both to
%LOCALAPPDATA%\LarsonServer\MedalSync\bin, keeping any previous copies. It does
not change any startup entry or shortcut; set-medal-startup.ps1 does that, as
a separately reviewed step in docs/medal-sync.md. Use -StateRoot for a
configured per-user installation outside AppData when packaged tools and the
ordinary desktop see different virtualized directories.
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'LarsonServer\MedalSync')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'medal-sync is published for Windows only.' }
if ($Install -and (Get-Process -Name 'medal-sync','medal-sync-launch' -ErrorAction SilentlyContinue)) {
    throw 'A medal-sync session is running. Quit Medal from its tray and wait for Ready before installing; publishing without -Install is safe.'
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$output = Join-Path $root '.medal-sync-dist'
$projects = @(
    @{ Project = 'tools\medal-sync\MedalSync\MedalSync.csproj'; Exe = 'medal-sync.exe'; Subsystem = 3 },
    @{ Project = 'tools\medal-sync\MedalSync.Launch\MedalSync.Launch.csproj'; Exe = 'medal-sync-launch.exe'; Subsystem = 2 }
)

# The PE header's subsystem field: 3 opens a console window, 2 does not.
function Get-Subsystem([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $header = [BitConverter]::ToInt32($bytes, 0x3C)
    return [BitConverter]::ToUInt16($bytes, $header + 92)
}

foreach ($item in $projects) {
    # RestoreLockedMode refuses package versions that differ from packages.lock.json.
    # The single-file analyzer would add an SDK-versioned package the lock file does
    # not pin, so it is off here; the checks below test the built exe instead.
    # Each project publishes to its own staging folder: the launcher's publish
    # would otherwise also drop files of the project it references.
    $staging = Join-Path (Join-Path $output 'staging') ([IO.Path]::GetFileNameWithoutExtension($item.Exe))
    $stagingBase = [IO.Path]::GetFullPath((Join-Path $output 'staging')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($staging).StartsWith($stagingBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish staging path escaped its expected directory.' }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    & dotnet publish (Join-Path $root $item.Project) -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true `
        -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o $staging -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $($item.Project) failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath (Join-Path $staging $item.Exe) -Destination $output -Force
    $exe = Join-Path $output $item.Exe
    $subsystem = Get-Subsystem $exe
    if ($subsystem -ne $item.Subsystem) { throw "$($item.Exe) has PE subsystem $subsystem, expected $($item.Subsystem)." }
}

# Only the console program can be run as a smoke test: the launcher would start a session.
$cli = Join-Path $output 'medal-sync.exe'
$version = & $cli version
if ($LASTEXITCODE -ne 0 -or -not $version) { throw "The published medal-sync.exe failed its smoke test (exit code $LASTEXITCODE)." }
Write-Host "Smoke test: $version"
foreach ($item in $projects) {
    $exe = Join-Path $output $item.Exe
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Built $exe"
    Write-Host "SHA-256 $hash"
}
$buildReceipt = [ordered]@{ format=1; builtAt=[DateTimeOffset]::UtcNow.ToString('o'); sdk=(& dotnet --version); files=@($projects | ForEach-Object {
    [ordered]@{ name=$_.Exe; sha256=(Get-FileHash -LiteralPath (Join-Path $output $_.Exe) -Algorithm SHA256).Hash.ToLowerInvariant() }
}) }
$buildReceipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build.json') -Encoding utf8NoBOM

if ($Install) {
    $bin = Join-Path ([IO.Path]::GetFullPath($StateRoot)) 'bin'
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    $suffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.previous'
    foreach ($item in $projects) {
        $target = Join-Path $bin $item.Exe
        if (Test-Path -LiteralPath $target) {
            $previous = "$target.$suffix"
            Move-Item -LiteralPath $target -Destination $previous
            Write-Host "Previous $($item.Exe) kept as $previous"
        }
        Copy-Item -LiteralPath (Join-Path $output $item.Exe) -Destination $target
        Write-Host "Installed $target"
    }
}
