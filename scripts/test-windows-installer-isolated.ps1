<# .SYNOPSIS
Installs, repairs, checks refusals and uninstalls a uniquely identified native setup using a synthetic readiness probe.
.DESCRIPTION
Uses production program payloads and the production Inno script with compile-time test identity.
Live Medal, user configuration, existing registration and program folders are never modified.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native installer integration requires Windows.' }
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'storage/medal-sync/MedalSync.Update.psm1') -Force
$version = Get-MedalBuildVersion $root
$id = [guid]::NewGuid().ToString('D')
$scratch = Join-Path $root ('.validation/native-isolated-' + $id)
$state = Join-Path $scratch 'synthetic-state'
$app = Join-Path $scratch 'Programs/Medal Sync'
$group = 'Medal Sync Test ' + $id
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $id + '_is1'
$shortcutGroup = Join-Path ([Environment]::GetFolderPath('Programs')) $group
$testSoftware = 'Software\MedalSyncTests\' + (Split-Path -Leaf $scratch)
if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $shortcutGroup)) { throw 'Unexpected test identity collision.' }
New-Item -ItemType Directory -Path $scratch,$state -Force | Out-Null
$compiler = & (Join-Path $PSScriptRoot 'setup-installer-toolchain.ps1')
$dependencies = & (Join-Path $PSScriptRoot 'setup-storage-toolchain.ps1')
$archive = Join-Path $root ".medal-sync-dist/medal-sync-$version-win-x64.zip"
$hash = ((Get-Content -LiteralPath "$archive.sha256" -Raw) -split '\s+')[0]
$payload = Join-Path $scratch 'payload'
$null = Read-MedalReleasePackage $archive $hash $payload
$probeDirectory = Join-Path $scratch 'probe'
& dotnet publish (Join-Path $root 'tests/integration/MedalSync.Integration.csproj') -c Release -r win-x64 --self-contained true `
    -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o $probeDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Isolated readiness probe publish failed.' }
$probe = Join-Path $probeDirectory 'MedalSync.Integration.exe'
function Build-IsolatedSetup([string]$BuildVersion, [string]$OutputDirectory) {
    $compile = @('/Qp',('/DAppVersion={0}' -f $BuildVersion),('/DPayloadDir={0}' -f $payload),('/DStorageDependencies={0}' -f $dependencies),
        ('/DOutputPath={0}' -f $OutputDirectory),('/DIsolatedTestId={0}' -f $id),('/DIsolatedTestRoot={0}' -f $state),('/DReadyChecker={0}' -f $probe),
        (Join-Path $root 'installer/windows/medal-sync.iss'))
    & $compiler @compile | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Isolated native setup compilation failed.' }
    return (Join-Path $OutputDirectory ('medal-sync-isolated-' + $id + '.exe'))
}
$setup = Build-IsolatedSetup $version $scratch
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="{0}"' -f $app),('/GROUP="{0}"' -f $group),('/LOG="{0}"' -f (Join-Path $scratch 'setup.log')))
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Output "PASS: $Message"
}
function Run-Setup([string]$Executable = $setup) {
    return (Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru).ExitCode
}
# Windows can hold an executable for a moment after it exits (for example while antivirus scans it),
# so a write right after running it may fail with a sharing violation. Retry for up to 10 seconds.
function Invoke-WhenUnlocked([scriptblock]$Action) {
    for ($attempt = 1; ; $attempt++) {
        try { & $Action; return }
        catch [System.IO.IOException] { if ($attempt -ge 20) { throw } }
        Start-Sleep -Milliseconds 500
    }
}
try {
    Assert-Check ((Run-Setup) -eq 0) 'isolated native installation succeeds'
    Assert-Check (Test-Path -LiteralPath $key) 'unique Installed apps registration exists'
    Assert-Check (Test-Path -LiteralPath (Join-Path $shortcutGroup 'Medal Sync.lnk')) 'native application shortcut exists'
    Assert-Check (Test-Path -LiteralPath (Join-Path $shortcutGroup 'Medal Sync Setup Guide.lnk')) 'offline guide shortcut exists'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $statusShortcut = $shell.CreateShortcut((Join-Path $shortcutGroup 'Medal Sync Status.lnk'))
        Assert-Check ($statusShortcut.TargetPath -eq (Join-Path $app 'bin/medal-sync-launch.exe') -and $statusShortcut.Arguments -eq '--status') 'native status shortcut opens the independent status view'
        $updateShortcut = $shell.CreateShortcut((Join-Path $shortcutGroup 'Check for Medal Sync updates.lnk'))
        Assert-Check ($updateShortcut.TargetPath -eq (Join-Path $app 'bin/medal-sync-launch.exe') -and $updateShortcut.Arguments -eq '--check-updates') 'native update shortcut uses the configuration-independent update entrypoint'
        $uninstallShortcut = $shell.CreateShortcut((Join-Path $shortcutGroup 'Uninstall Medal Sync.lnk'))
        Assert-Check ($uninstallShortcut.TargetPath -eq (Join-Path $app 'unins000.exe')) 'uninstall shortcut targets the bundled native uninstaller EXE'
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    Assert-Check ((Test-Path -LiteralPath (Join-Path $app 'unins000.exe')) -and (Test-Path -LiteralPath (Join-Path $app 'unins000.dat'))) 'installer deploys the generated uninstaller executable and its installation log'
    Assert-Check ((Get-ItemProperty -LiteralPath $key).UninstallString -like ('*' + (Join-Path $app 'unins000.exe') + '*')) 'Installed apps invokes the bundled uninstaller'
    foreach ($name in @('medal-sync.exe','medal-sync-launch.exe')) {
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $app "bin/$name")).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payload $name)).Hash) "installed $name matches the verified release payload"
    }
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $app 'bin/rclone.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $dependencies 'rclone.exe')).Hash) 'bundled rclone matches its verified dependency'
    $reportedVersion = & (Join-Path $app 'bin/medal-sync.exe') version
    Assert-Check ($LASTEXITCODE -eq 0 -and ($reportedVersion -join '') -like "medal-sync $version;*") 'installed production CLI starts and reports the packaged version'
    $candidate = Join-Path $scratch 'candidate.json'
    $candidateOutput = & (Join-Path $app 'bin/medal-sync.exe') config create --storage-mode local --account-id 12345 --new-library `
        --remote (Join-Path $scratch 'persistent-media') --cache-root (Join-Path $scratch 'cache') --coordination-root (Join-Path $scratch 'journal') --out $candidate --apply
    Assert-Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $candidate)) 'installed CLI creates a valid isolated local-storage candidate'
    $candidateConfig = Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json
    Assert-Check ($candidateConfig.rclonePath -eq (Join-Path $app 'bin/rclone.exe')) 'installed configuration selects bundled rclone automatically'
    $candidateValidation = & (Join-Path $app 'bin/medal-sync.exe') config validate --config $candidate
    Assert-Check ($LASTEXITCODE -eq 0) 'installed CLI validates the isolated candidate without accessing a live library'
    $sentinel = Join-Path $app 'unowned-user-file.txt'
    'preserve me' | Set-Content -LiteralPath $sentinel
    Invoke-WhenUnlocked { Set-Content -LiteralPath (Join-Path $app 'bin/medal-sync.exe') -Value 'synthetic damaged executable' }
    Assert-Check ((Run-Setup) -eq 0) 'native in-place repair recovers a damaged CLI using the registered version'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $app 'bin/medal-sync.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payload 'medal-sync.exe')).Hash) 'repair restores the verified CLI bytes'
    Invoke-WhenUnlocked { Remove-Item -LiteralPath (Join-Path $app 'bin/medal-sync.exe') }
    Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value '99.0.0'
    try { Assert-Check ((Run-Setup) -ne 0) 'native setup refuses a registered downgrade even when the CLI is missing' }
    finally { Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $version }
    Assert-Check ((Run-Setup) -eq 0) 'native repair restores a missing CLI without losing registration'
    $stableVersion = $version -replace '-beta\.\d+$', ''
    $betaVersion = "$stableVersion-beta.2"
    $betaSetup = if ($version -eq $betaVersion) { $setup } else { Build-IsolatedSetup $betaVersion (Join-Path $scratch 'beta') }
    $stableSetup = if ($version -eq $stableVersion) { $setup } else { Build-IsolatedSetup $stableVersion (Join-Path $scratch 'stable') }
    try {
        Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $stableVersion
        Assert-Check ((Run-Setup $betaSetup) -ne 0) 'native setup refuses a beta over its stable release'
        Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value "$stableVersion-beta.10"
        Assert-Check ((Run-Setup $betaSetup) -ne 0) 'native setup refuses an older beta over a newer beta'
        Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value "$stableVersion-beta.1"
        Assert-Check ((Run-Setup $betaSetup) -eq 0) 'native setup installs a newer beta over an earlier beta'
        Assert-Check ((Run-Setup $betaSetup) -eq 0) 'native setup repairs the same beta'
        Assert-Check ((Run-Setup $stableSetup) -eq 0) 'native setup installs a stable release over its beta'
        Assert-Check ((Get-ItemProperty -LiteralPath $key).DisplayVersion -eq $stableVersion) 'native setup registers the stable version'
    } finally { Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $version }
    $binConfig = Join-Path $app 'bin/config.json'
    Set-Content -LiteralPath $binConfig -Value 'synthetic user configuration'
    Assert-Check ((Run-Setup) -ne 0) 'native setup refuses user configuration in the program bin directory'
    Assert-Check ((Get-Content -LiteralPath $binConfig -Raw).Trim() -eq 'synthetic user configuration') 'refused setup retains misplaced configuration'
    Remove-Item -LiteralPath $binConfig
    $relocated = Start-Process -FilePath $setup -ArgumentList ($arguments + ('/DIR="{0}"' -f (Join-Path $scratch 'other-programs'))) -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($relocated.ExitCode -ne 0) 'native upgrade refuses leaving an existing registered installation in another folder'
    $pending = Join-Path $state 'sessions'
    New-Item -ItemType Directory -Path $pending | Out-Null
    Set-Content -LiteralPath (Join-Path $pending 'synthetic.json') -Value '{}'
    Assert-Check ((Run-Setup) -ne 0) 'native upgrade refuses retained session evidence'
    Remove-Item -LiteralPath (Join-Path $pending 'synthetic.json')
    Set-Content -LiteralPath (Join-Path $state 'application-running') -Value 'synthetic process'
    Assert-Check ((Run-Setup) -ne 0) 'native upgrade refuses the synthetic running-app probe'
    Remove-Item -LiteralPath (Join-Path $state 'application-running')
    $mutex = [Threading.Mutex]::new($false, ('Local\MedalSync.Test.App.' + $id))
    try { Assert-Check ((Run-Setup) -ne 0) 'native setup refuses its application mutex' } finally { $mutex.Dispose() }
    $setupMutex = [Threading.Mutex]::new($false, ('Local\MedalSync.Test.Setup.' + $id))
    try {
        $busy = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
        Assert-Check ($busy.ExitCode -ne 0) 'native uninstall refuses concurrent setup'
    } finally { $setupMutex.Dispose() }
    $secondCheck = Join-Path $state 'refuse-second-check'
    Set-Content -LiteralPath $secondCheck -Value 'synthetic readiness change'
    $changed = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($changed.ExitCode -ne 0 -and (Test-Path -LiteralPath $key) -and (Test-Path -LiteralPath (Join-Path $app 'bin/medal-sync.exe'))) 'uninstaller rechecks readiness before removal and preserves programs when it changes'
    Remove-Item -LiteralPath $secondCheck
    Remove-Item -LiteralPath (Join-Path $state 'first-check-seen')
    $startupLog = Join-Path $scratch 'uninstall-startup.log'
    $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$testSoftware\Microsoft\Windows\CurrentVersion\Run")
    try {
        $run.SetValue('MedalSync', ('"{0}" --at-sign-in' -f (Join-Path $app 'bin/medal-sync-launch.exe')))
        $integrated = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="{0}"' -f $startupLog)) -WindowStyle Hidden -Wait -PassThru
        Assert-Check ($integrated.ExitCode -ne 0 -and (Test-Path -LiteralPath (Join-Path $app 'bin/medal-sync-launch.exe'))) 'native uninstall preserves the launcher while Medal still uses it at sign-in'
        Assert-Check ((Get-Content -LiteralPath $startupLog -Raw) -match 'startup --restore') 'native uninstall explains how to restore Medal startup'
        $run.DeleteValue('MedalSync')
    } finally { $run.Dispose() }
    $uninstall = Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($uninstall.ExitCode -eq 0) 'isolated native uninstall succeeds'
    Assert-Check (-not (Test-Path -LiteralPath $key)) 'uninstall removes only its unique registration'
    Assert-Check (-not (Test-Path -LiteralPath $shortcutGroup)) 'uninstall removes its shortcut group'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $app 'bin/medal-sync.exe'))) 'uninstall removes its managed programs'
    Assert-Check ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'preserve me') 'uninstall retains unowned user data'
    @{ format=1; id=$id; checks=$checks; version=$version; setupSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant(); completedAt=[DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'verified.json') -Encoding utf8NoBOM
    Write-Output "PASS: $checks native installer assertions; receipt $scratch/verified.json"
} catch {
    Write-Output "Isolated test resources and logs retained at $scratch. Existing installations were not used."
    throw
} finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testSoftware, $false)
}
