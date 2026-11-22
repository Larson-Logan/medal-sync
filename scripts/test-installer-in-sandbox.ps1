<# .SYNOPSIS
Smoke-tests a built Medal Sync setup EXE on a clean Windows in Windows Sandbox.
.DESCRIPTION
Starts a disposable Windows Sandbox with networking off, copies the setup EXE (and optionally a
previous release's setup EXE) into a read-only mapped folder and runs scripts/sandbox/installer-smoke.ps1
at sign-in. Inside the sandbox it does a silent per-user install and uninstall, a silent all-users
install and uninstall, and, with -PreviousSetup, an upgrade from that release in both scopes. Each step
checks the Installed apps entry (display name, version, publisher, product code and scope), the program
folder, the Start menu shortcuts and WinFsp.

The sandbox writes result.json, a transcript and every setup log to the results folder. This script
waits for them, closes the sandbox, prints pass or fail per step and exits 1 when any step failed.
It skips with exit code 0 in CI and on PCs where Windows Sandbox is not enabled. Starting it closes
any Windows Sandbox that is already open, because Windows runs only one at a time.
.EXAMPLE
pwsh -File scripts/test-installer-in-sandbox.ps1 -Setup .medal-sync-dist/medal-sync-1.6.0.00-setup-win-x64.exe
.EXAMPLE
pwsh -File scripts/test-installer-in-sandbox.ps1 -Setup new-setup.exe -PreviousSetup medal-sync-1.5.0.00-setup-win-x64.exe
#>
[CmdletBinding()]
param(
    # The setup EXE to test.
    [Parameter(Mandatory)] [string]$Setup,
    # An earlier release's setup EXE. Adds an upgrade from it to -Setup in both scopes.
    [string]$PreviousSetup,
    # Where the run folder goes. Defaults to .validation/sandbox-installer-TIMESTAMP in the repository.
    [string]$ResultsDirectory,
    [ValidateRange(5, 240)] [int]$TimeoutMinutes = 30,
    [ValidateRange(2048, 65536)] [int]$MemoryMB = 4096,
    # Setup needs no network. Turn it on only to investigate something that does.
    [switch]$Networking,
    # Leave the sandbox open after the run to inspect it. Close it yourself afterwards.
    [switch]$KeepSandbox
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SandboxRoot = 'C:\MedalSyncTest'
$StartupSeconds = 120
$PollSeconds = 5
$VersionPattern = '^\d+\.\d+\.\d+\.\d+(-beta\.\d+)?$'

function Skip-Test([string]$Reason) {
    Write-Output "SKIP: $Reason"
    exit 0
}

if ($env:GITHUB_ACTIONS -eq 'true' -or $env:CI) { Skip-Test 'Windows Sandbox installer tests run only on a maintainer PC, never in CI.' }
if (-not $IsWindows) { Skip-Test 'Windows Sandbox needs Windows.' }
$sandboxExe = Join-Path $env:windir 'System32\WindowsSandbox.exe'
if (-not (Test-Path -LiteralPath $sandboxExe)) {
    Skip-Test 'Windows Sandbox is not enabled. Turn on the "Windows Sandbox" optional feature (Windows Pro, Enterprise or Education) and restart.'
}

function Get-SetupInfo([string]$Path, [string]$Label) {
    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $item -or $item.PSIsContainer) { throw "$Label $Path does not exist." }
    # Inno Setup pads its version resource strings with spaces.
    $product = "$($item.VersionInfo.ProductName)".Trim()
    $version = "$($item.VersionInfo.ProductVersion)".Trim()
    if ($product -ne 'Medal Sync' -or $version -notmatch $VersionPattern) {
        throw "$Label $Path is not a Medal Sync setup EXE (product '$product', version '$version')."
    }
    return [pscustomobject]@{
        Path = $item.FullName; Name = $item.Name; Version = $version
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

# WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession and WindowsSandboxServer all
# belong to an open sandbox; which ones run depends on the Windows build (24H2 adds WindowsSandbox).
function Get-SandboxProcess {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'WindowsSandbox*' })
}

function Wait-SandboxClosed([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (@(Get-SandboxProcess).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    return @(Get-SandboxProcess).Count -eq 0
}

function Stop-Sandbox {
    $running = @(Get-SandboxProcess)
    if ($running.Count -eq 0) { return }
    Write-Output "Closing Windows Sandbox ($(($running | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', '))."
    foreach ($process in $running) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { Write-Verbose "Process $($process.Id): $($_.Exception.Message)" }
    }
    if (-not (Wait-SandboxClosed 30)) {
        $left = (@(Get-SandboxProcess) | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', '
        throw "Windows Sandbox did not close ($left). Close it, then run this script again."
    }
}

function Write-Report($Result) {
    foreach ($step in $Result.steps) {
        $label = if ($step.skipped) { 'SKIP' } elseif ($step.passed) { 'PASS' } else { 'FAIL' }
        Write-Output ('{0}  {1,-28} {2,6:0.0}s' -f $label, $step.name, $step.durationSeconds)
        if ($step.error) { Write-Output "      $($step.error)" }
        foreach ($check in @($step.checks | Where-Object { -not $_.passed })) {
            $detail = if ($check.detail) { " ($($check.detail))" } else { '' }
            Write-Output "      failed: $($check.name)$detail"
        }
    }
    if ($Result.fatal) { Write-Output "FATAL: $($Result.fatal)" }
}

$current = Get-SetupInfo $Setup 'Setup'
$previous = $null
if ($PreviousSetup) {
    $previous = Get-SetupInfo $PreviousSetup 'Previous setup'
    if ($previous.Version -eq $current.Version) { throw "The previous setup is the same version, $($current.Version)." }
    if ($previous.Name -eq $current.Name) { throw 'The setup and previous setup have the same file name.' }
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $root ('.validation/sandbox-installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$work = [IO.Path]::GetFullPath($ResultsDirectory)
if ((Test-Path -LiteralPath $work) -and @(Get-ChildItem -LiteralPath $work -Force).Count -gt 0) {
    throw "$work is not empty. Choose a new results directory."
}
$inputDirectory = Join-Path $work 'input'
$results = Join-Path $work 'results'
New-Item -ItemType Directory -Path $inputDirectory, $results -Force | Out-Null

Copy-Item -LiteralPath $current.Path -Destination $inputDirectory
if ($previous) { Copy-Item -LiteralPath $previous.Path -Destination $inputDirectory }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sandbox/installer-smoke.ps1') -Destination $inputDirectory
[ordered]@{
    setup = $current.Name; version = $current.Version; setupSha256 = $current.Sha256
    previousSetup = $previous ? $previous.Name : $null; previousVersion = $previous ? $previous.Version : $null
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDirectory 'run.json') -Encoding utf8NoBOM

$guestInput = "$SandboxRoot\input"
$guestResults = "$SandboxRoot\results"
$command = "cmd.exe /c powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $guestInput\installer-smoke.ps1 " +
    "-InputDirectory $guestInput -ResultsDirectory $guestResults > $guestResults\console.txt 2>&1"
function ConvertTo-Xml([string]$Value) { return [Security.SecurityElement]::Escape($Value) }
$configuration = @"
<Configuration>
  <Networking>$(if ($Networking) { 'Default' } else { 'Disable' })</Networking>
  <vGPU>Disable</vGPU>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MemoryInMB>$MemoryMB</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(ConvertTo-Xml $inputDirectory)</HostFolder>
      <SandboxFolder>$guestInput</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(ConvertTo-Xml $results)</HostFolder>
      <SandboxFolder>$guestResults</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>$(ConvertTo-Xml $command)</Command>
  </LogonCommand>
</Configuration>
"@
$wsb = Join-Path $work 'installer-smoke.wsb'
Set-Content -LiteralPath $wsb -Value $configuration -Encoding utf8NoBOM

Stop-Sandbox
Write-Output "Testing $($current.Name) ($($current.Version))$(if ($previous) { ", upgrade from $($previous.Name)" })"
Write-Output "Run folder: $work"
Start-Process -FilePath $sandboxExe -ArgumentList ('"{0}"' -f $wsb) | Out-Null

$resultPath = Join-Path $results 'result.json'
$started = Get-Date
$deadline = $started.AddMinutes($TimeoutMinutes)
$seenSandbox = $false
$failure = $null
while (-not (Test-Path -LiteralPath $resultPath)) {
    $open = @(Get-SandboxProcess).Count -gt 0
    $seenSandbox = $seenSandbox -or $open
    if ((Get-Date) -gt $deadline) { $failure = "No result after $TimeoutMinutes minutes."; break }
    if (-not $open -and ($seenSandbox -or (Get-Date) -gt $started.AddSeconds($StartupSeconds))) {
        # The guest may have written the result just before the sandbox closed.
        if (-not (Test-Path -LiteralPath $resultPath)) { $failure = 'Windows Sandbox closed or did not start before writing a result.' }
        break
    }
    Start-Sleep -Seconds $PollSeconds
}

if ($KeepSandbox) {
    Write-Output 'The sandbox stays open (-KeepSandbox). Close it when you are done.'
} else {
    Stop-Sandbox
}

if ($failure) {
    Write-Output "FAIL: $failure"
    Write-Output "Inspect $results (console.txt, transcript.txt and logs)."
    exit 1
}
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
Write-Output "Windows $($result.windows) in the sandbox; $([Math]::Round(((Get-Date) - $started).TotalMinutes, 1)) minutes."
Write-Report $result
if (-not $result.passed) {
    Write-Output "FAIL: installer smoke test. Setup logs and transcript: $results"
    exit 1
}
Write-Output "PASS: $(@($result.steps).Count) installer steps in Windows Sandbox. Results: $results"
