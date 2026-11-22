<# .SYNOPSIS
Runs inside Windows Sandbox: installs, upgrades and uninstalls Medal Sync setup EXEs and records each step.
.DESCRIPTION
Started by scripts/test-installer-in-sandbox.ps1 through the sandbox logon command. The sandbox ships
only Windows PowerShell 5.1, so this script avoids PowerShell 7 syntax. It reads run.json from the
read-only input folder and writes result.json, a transcript and setup logs to the results folder.
Never run it on a real PC: it installs and removes Medal Sync and WinFsp machine-wide.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$InputDirectory,
    [Parameter(Mandatory)] [string]$ResultsDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductCode = '{168562DF-1041-441D-96D0-2244EA744E18}_is1'
$UninstallPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $ProductCode
$Publisher = 'Medal Sync contributors'
$ShortcutNames = @('Medal Sync', 'Medal Sync Status', 'Medal Sync Setup Guide', 'Check for Medal Sync updates', 'Uninstall Medal Sync')
$ProgramFiles = @('bin\medal-sync.exe', 'bin\medal-sync-launch.exe', 'bin\rclone.exe', 'bin\setup-layout.json',
    'setup.html', 'LICENSE', 'RCLONE-LICENSE.txt', 'unins000.exe', 'unins000.dat')
$SilentArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
$ProcessTimeoutSeconds = 600
$RemovalWaitSeconds = 60

$logs = Join-Path $ResultsDirectory 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Start-Transcript -LiteralPath (Join-Path $ResultsDirectory 'transcript.txt') -Force | Out-Null
$run = Get-Content -LiteralPath (Join-Path $InputDirectory 'run.json') -Raw | ConvertFrom-Json
$steps = New-Object System.Collections.Generic.List[object]
$script:current = $null

function Get-Scope([string]$Scope) {
    if ($Scope -eq 'machine') {
        return [pscustomobject]@{
            Name = 'machine'; Switch = '/ALLUSERS'; Hive = 'HKLM:'; OtherHive = 'HKCU:'
            Directory = Join-Path $env:ProgramFiles 'Medal Sync'
            Group = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Medal Sync'
        }
    }
    return [pscustomobject]@{
        Name = 'user'; Switch = '/CURRENTUSER'; Hive = 'HKCU:'; OtherHive = 'HKLM:'
        Directory = Join-Path $env:LOCALAPPDATA 'Programs\Medal Sync'
        Group = Join-Path ([Environment]::GetFolderPath('Programs')) 'Medal Sync'
    }
}

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:current.checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $mark = 'FAIL'
    if ($Passed) { $mark = 'PASS' }
    if ($Detail) { Write-Host "  ${mark}: $Name ($Detail)" } else { Write-Host "  ${mark}: $Name" }
}

# The body sees its caller's variables, so these parameter names must not shadow them.
function Invoke-Step([string]$StepName, [string]$StepScope, [scriptblock]$Body) {
    $script:current = [pscustomobject]@{
        name = $StepName; scope = $StepScope; passed = $false; skipped = $false; exitCode = $null
        durationSeconds = 0; error = $null; log = $null
        checks = New-Object System.Collections.Generic.List[object]
    }
    Write-Host "STEP: $StepName"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try { $null = & $Body }
    catch { $script:current.error = $_.Exception.Message; Write-Host "  ERROR: $($_.Exception.Message)" }
    $clock.Stop()
    $script:current.durationSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    $failed = @($script:current.checks | Where-Object { -not $_.passed })
    $script:current.passed = (-not $script:current.error) -and $failed.Count -eq 0 -and $script:current.checks.Count -gt 0
    $steps.Add($script:current)
    return $script:current.passed
}

function Add-SkippedStep([string]$Name, [string]$Scope, [string]$Reason) {
    $steps.Add([pscustomobject]@{
        name = $Name; scope = $Scope; passed = $false; skipped = $true; exitCode = $null
        durationSeconds = 0; error = $Reason; log = $null; checks = @()
    })
    Write-Host "SKIP: $Name ($Reason)"
}

function Invoke-Program([string]$Path, [string[]]$Arguments, [string]$LogName) {
    $log = Join-Path $logs "$LogName.log"
    $script:current.log = "logs/$LogName.log"
    $all = $Arguments + ('/LOG="{0}"' -f $log)
    $process = Start-Process -FilePath $Path -ArgumentList $all -WindowStyle Hidden -PassThru
    # Inno uninstallers relaunch themselves from TEMP; the first process waits for and reports the second.
    if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
        $process.Kill()
        throw "$([IO.Path]::GetFileName($Path)) did not finish within $ProcessTimeoutSeconds seconds."
    }
    $script:current.exitCode = $process.ExitCode
    return $process.ExitCode
}

function Get-Registration([string]$Hive) {
    return Get-ItemProperty -LiteralPath (Join-Path $Hive $UninstallPath) -ErrorAction SilentlyContinue
}

function Test-WinFsp {
    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry32').OpenSubKey('SOFTWARE\WinFsp')
    if (-not $key) { return $false }
    try { $directory = [string]$key.GetValue('InstallDir', '') } finally { $key.Dispose() }
    return $directory -and (Test-Path -LiteralPath (Join-Path $directory 'bin\winfsp-x64.dll'))
}

function Test-SamePath([string]$Left, [string]$Right) {
    if (-not $Left -or -not $Right) { return $false }
    return [IO.Path]::GetFullPath($Left).TrimEnd('\') -ieq [IO.Path]::GetFullPath($Right).TrimEnd('\')
}

function Assert-Installed($Scope, [string]$Version, [bool]$ExpectWinFsp, [switch]$Previous) {
    $registration = Get-Registration $Scope.Hive
    Add-Check "Installed apps entry $ProductCode exists in $($Scope.Hive)" ($null -ne $registration)
    Add-Check "no Installed apps entry in $($Scope.OtherHive)" ($null -eq (Get-Registration $Scope.OtherHive))
    if ($registration) {
        # UninstallDisplayName in medal-sync.iss; the winget manifest declares the same name.
        # Releases up to 1.6.0.00 set none, so Inno Setup appended the version.
        $names = if ($Previous) { @('Medal Sync', "Medal Sync $Version") } else { @('Medal Sync') }
        Add-Check 'display name is Medal Sync' ($names -ccontains $registration.DisplayName) $registration.DisplayName
        Add-Check "display version is $Version" ($registration.DisplayVersion -eq $Version) $registration.DisplayVersion
        Add-Check "publisher is $Publisher" ($registration.Publisher -eq $Publisher) $registration.Publisher
        Add-Check 'install location is the default for the scope' (Test-SamePath $registration.InstallLocation $Scope.Directory) $registration.InstallLocation
        $uninstaller = Join-Path $Scope.Directory 'unins000.exe'
        Add-Check 'uninstall command runs the bundled uninstaller' ($registration.UninstallString -like "*$uninstaller*") $registration.UninstallString
        $quiet = $registration.PSObject.Properties['QuietUninstallString']
        Add-Check 'quiet uninstall command is registered' ($null -ne $quiet -and $quiet.Value -like '*/SILENT*')
    }
    $missing = @($ProgramFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Scope.Directory $_)) })
    Add-Check 'program folder holds every installed file' ($missing.Count -eq 0) ($missing -join ', ')
    $cli = Join-Path $Scope.Directory 'bin\medal-sync.exe'
    if (Test-Path -LiteralPath $cli) {
        $numeric = ($Version -split '-')[0]
        # Windows stores the hotfix counter as a number, so 1.6.0.00 reads back as 1.6.0.0.
        $fileVersion = (Get-Item -LiteralPath $cli).VersionInfo.FileVersion
        Add-Check "medal-sync.exe file version is $numeric" ([version]$fileVersion -eq [version]$numeric) $fileVersion
        $reported = (& $cli version 2>&1 | Out-String).Trim()
        Add-Check 'installed CLI starts and reports the setup version' ($LASTEXITCODE -eq 0 -and $reported -like "medal-sync $Version;*") $reported
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($name in $ShortcutNames) {
            $path = Join-Path $Scope.Group "$name.lnk"
            $exists = Test-Path -LiteralPath $path
            $target = ''
            if ($exists) { $target = $shell.CreateShortcut($path).TargetPath }
            Add-Check "Start menu shortcut '$name' points into the program folder" ($exists -and $target -like "$($Scope.Directory)\*") $target
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Medal Sync.lnk'
    $commonDesktop = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Medal Sync.lnk'
    Add-Check 'no desktop shortcut by default' (-not (Test-Path -LiteralPath $desktop) -and -not (Test-Path -LiteralPath $commonDesktop))
    if ($ExpectWinFsp) { Add-Check 'WinFsp is installed' (Test-WinFsp) }
    else { Add-Check 'WinFsp is not installed by this setup' (-not (Test-WinFsp)) }
}

function Get-LeftoverFile($Scope) {
    return @($ProgramFiles | Where-Object { Test-Path -LiteralPath (Join-Path $Scope.Directory $_) })
}

function Assert-Removed($Scope, [bool]$ExpectWinFsp) {
    # The uninstaller deletes unins000.exe from its TEMP copy just after the first process exits.
    $deadline = (Get-Date).AddSeconds($RemovalWaitSeconds)
    while (((Get-Registration $Scope.Hive) -or @(Get-LeftoverFile $Scope).Count -gt 0) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    Add-Check 'Installed apps entry is removed' ($null -eq (Get-Registration $Scope.Hive))
    $left = @(Get-LeftoverFile $Scope)
    Add-Check 'installed program files are removed' ($left.Count -eq 0) ($left -join ', ')
    Add-Check 'Start menu group is removed' (-not (Test-Path -LiteralPath $Scope.Group))
    if ($ExpectWinFsp) { Add-Check 'WinFsp is kept' (Test-WinFsp) }
}

function Install-Setup($Scope, [string]$Setup, [string]$LogName) {
    $exit = Invoke-Program (Join-Path $InputDirectory $Setup) ($SilentArguments + $Scope.Switch) $LogName
    Add-Check 'setup exits with code 0' ($exit -eq 0) "exit code $exit"
}

function Uninstall-Setup($Scope, [string]$LogName) {
    $uninstaller = Join-Path $Scope.Directory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw "Uninstaller $uninstaller is missing." }
    $exit = Invoke-Program $uninstaller $SilentArguments $LogName
    Add-Check 'uninstaller exits with code 0' ($exit -eq 0) "exit code $exit"
}

# Leaves the sandbox ready for the next sequence even when a step failed part-way.
function Remove-Leftovers($Scope) {
    $uninstaller = Join-Path $Scope.Directory 'unins000.exe'
    if ((Get-Registration $Scope.Hive) -and (Test-Path -LiteralPath $uninstaller)) {
        Write-Host "CLEANUP: uninstalling the $($Scope.Name) installation left by a failed step"
        $process = Start-Process -FilePath $uninstaller -ArgumentList $SilentArguments -WindowStyle Hidden -PassThru
        [void]$process.WaitForExit($ProcessTimeoutSeconds * 1000)
    }
}

# One install, check and uninstall of the current setup. WinFsp stays after the all-users uninstall,
# so the per-user sequence runs first to see a machine without it.
function Test-FreshInstall([string]$ScopeName) {
    $scope = Get-Scope $ScopeName
    $winFspBefore = Test-WinFsp
    $expectWinFsp = $ScopeName -eq 'machine' -or $winFspBefore
    $installed = Invoke-Step "$ScopeName-install" $ScopeName {
        Install-Setup $scope $run.setup "$ScopeName-install"
        Assert-Installed $scope $run.version $expectWinFsp
    }
    if ((Get-Registration $scope.Hive) -and (Test-Path -LiteralPath (Join-Path $scope.Directory 'unins000.exe'))) {
        [void](Invoke-Step "$ScopeName-uninstall" $ScopeName {
            Uninstall-Setup $scope "$ScopeName-uninstall"
            Assert-Removed $scope $expectWinFsp
        })
    } else {
        Add-SkippedStep "$ScopeName-uninstall" $ScopeName 'nothing was installed to uninstall'
    }
    if (-not $installed) { Remove-Leftovers $scope }
}

function Test-Upgrade([string]$ScopeName) {
    $scope = Get-Scope $ScopeName
    $expectWinFsp = $ScopeName -eq 'machine' -or (Test-WinFsp)
    $sentinel = Join-Path $scope.Directory 'user-file-kept-by-upgrade.txt'
    [void](Invoke-Step "$ScopeName-install-previous" $ScopeName {
        Install-Setup $scope $run.previousSetup "$ScopeName-install-previous"
        Assert-Installed $scope $run.previousVersion $expectWinFsp -Previous
        if (Test-Path -LiteralPath $scope.Directory) { Set-Content -LiteralPath $sentinel -Value 'preserve me' }
    })
    # A failed check still leaves something to upgrade; only a missing installation stops the sequence.
    if (-not (Get-Registration $scope.Hive)) {
        Add-SkippedStep "$ScopeName-upgrade" $ScopeName 'the previous release did not install'
        Add-SkippedStep "$ScopeName-uninstall-upgraded" $ScopeName 'the previous release did not install'
        Remove-Leftovers $scope
        return
    }
    $upgraded = Invoke-Step "$ScopeName-upgrade" $ScopeName {
        Install-Setup $scope $run.setup "$ScopeName-upgrade"
        Assert-Installed $scope $run.version $expectWinFsp
        Add-Check 'upgrade keeps unowned files in the program folder' ((Test-Path -LiteralPath $sentinel) -and (Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'preserve me')
    }
    [void](Invoke-Step "$ScopeName-uninstall-upgraded" $ScopeName {
        Uninstall-Setup $scope "$ScopeName-uninstall-upgraded"
        Assert-Removed $scope $expectWinFsp
        Add-Check 'uninstall keeps unowned files in the program folder' (Test-Path -LiteralPath $sentinel)
    })
    if (-not $upgraded) { Remove-Leftovers $scope }
}

$started = [DateTimeOffset]::UtcNow
$fatal = $null
$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
try {
    Write-Host "Windows $($os.DisplayVersion) build $($os.CurrentBuild).$($os.UBR); user $env:USERNAME"
    Write-Host "Testing $($run.setup) ($($run.version))"
    Test-FreshInstall 'user'
    Test-FreshInstall 'machine'
    if ($run.previousSetup) {
        Write-Host "Upgrading from $($run.previousSetup) ($($run.previousVersion))"
        Test-Upgrade 'user'
        Test-Upgrade 'machine'
    }
} catch {
    $fatal = $_.Exception.Message
    Write-Host "FATAL: $fatal"
} finally {
    $failed = @($steps | Where-Object { -not $_.passed })
    $result = [pscustomobject]@{
        format = 1
        passed = (-not $fatal) -and $steps.Count -gt 0 -and $failed.Count -eq 0
        fatal = $fatal
        setup = $run.setup; version = $run.version
        previousSetup = $run.previousSetup; previousVersion = $run.previousVersion
        windows = "$($os.DisplayVersion) build $($os.CurrentBuild).$($os.UBR)"
        startedAt = $started.ToString('o'); completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        steps = $steps
    }
    Stop-Transcript | Out-Null
    # The host waits for result.json, so write it whole and last.
    $temporary = Join-Path $ResultsDirectory 'result.json.tmp'
    [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temporary -Destination (Join-Path $ResultsDirectory 'result.json') -Force
}
