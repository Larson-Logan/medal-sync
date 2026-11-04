# Shared by packaging, the updater and synthetic tests. No runtime dependencies.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Payload = @('medal-sync.exe', 'medal-sync-launch.exe', 'update-medal-sync.ps1', 'MedalSync.Update.psm1', 'set-medal-startup.ps1', 'LICENSE', 'THIRD_PARTY.md', 'DOTNET-LICENSE.txt', 'DOTNET-NOTICES.txt')

function Get-MedalPayloadNames { return $script:Payload.Clone() }

# Named objects shared with native setup and the CLI. Tests replace them with isolated names.
$script:SetupMutexName = 'Global\LarsonServer.MedalSync.Setup'
$script:InstanceMutexName = 'Local\LarsonServer.MedalSync'
$script:StatusViewMutexName = 'Local\LarsonServer.MedalSync.StatusView'
$script:Maintenance = $null
$script:StatusViewOpen = 'Only the Medal Sync status view is still open. Choose Exit Medal Sync in the status panel or tray menu, then try again. Older versions call this Exit status view (supervision continues).'

# MAJOR.MINOR.PATCH.NN, where NN is the two-digit hotfix counter. Versions
# before 1.3.0.00 had no counter, so installed programs may still report three parts.
$script:StableVersion = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0[0-9]|[1-9][0-9]+))?'
# Beta builds from dev add -beta.N to the four-part release they lead up to.
$script:Prerelease = 'beta\.[1-9][0-9]*'

function Assert-MedalVersion([string]$Version) {
    if ($Version -cnotmatch "^$script:StableVersion$") { throw 'Expected a stable version such as 0.2.0.01.' }
    return [version]$Version
}

# Any version a build can carry: a stable version, or a four-part one with -beta.N.
function Assert-MedalBuildVersion([string]$Version) {
    $stable = $Version -creplace "-$script:Prerelease$", ''
    if ($stable -cne $Version -and $stable.Split('.').Count -ne 4) { throw 'Expected a beta version such as 0.2.0.01-beta.1.' }
    $null = Assert-MedalVersion $stable
}

function Get-MedalBuildVersion([string]$Root) {
    # VersionPrefix from Directory.Build.props plus the hotfix counter, which the
    # release workflow passes in the MedalReleaseRevision environment variable,
    # and, for beta builds from dev, the MedalPrerelease label such as beta.3.
    $props = [xml](Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw)
    $prefix = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
    $revision = if ($env:MedalReleaseRevision) { $env:MedalReleaseRevision } else { $props.SelectSingleNode('/Project/PropertyGroup/MedalReleaseRevision').InnerText }
    if ($revision -cnotmatch '^(0[0-9]|[1-9][0-9]+|none)$') { throw "MedalReleaseRevision '$revision' must be two or more digits, such as 00 or 01, or none." }
    # "none" is the one-time three-part bridge release for pre-1.3.0 updaters.
    $version = if ($revision -ceq 'none') { $prefix } else { "$prefix.$revision" }
    $null = Assert-MedalVersion $version
    $prerelease = if ($env:MedalPrerelease) { $env:MedalPrerelease } else { '' }
    if ($prerelease) {
        if ($prerelease -cnotmatch "^$script:Prerelease$") { throw "MedalPrerelease '$prerelease' must be beta.N, such as beta.1." }
        if ($revision -ceq 'none') { throw 'A beta build needs a hotfix counter, not none.' }
        $version = "$version-$prerelease"
    }
    return $version
}

function Read-MedalReleasePackage([string]$PackagePath, [string]$ExpectedSha256, [string]$Destination) {
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'A SHA-256 digest from the release is required.' }
    if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ine $ExpectedSha256) { throw 'Release archive SHA-256 mismatch.' }
    if (Test-Path -LiteralPath $Destination) { throw 'Extraction destination must be new.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        $expected = @('release.json') + $script:Payload
        if ($names.Count -ne $expected.Count -or @($names | Select-Object -Unique).Count -ne $expected.Count) { throw 'Unexpected or duplicate release archive entries.' }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $expected -or $entry.Length -gt 200MB -or $entry.Length -lt 1) { throw 'Unexpected release archive path or size.' }
        }
        if ($zip.GetEntry('release.json').Length -gt 128KB) { throw 'Release manifest is too large.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('release.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.format -ne 1 -or $manifest.runtime -cne 'win-x64') { throw 'Unsupported release manifest.' }
        Assert-MedalBuildVersion $manifest.version
        if (@($manifest.files).Count -ne $script:Payload.Count) { throw 'Incomplete release manifest.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $manifest.files) {
            if ($file.name -cnotin $script:Payload -or -not $seen.Add($file.name) -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid release file record.' }
        }
        $null = New-Item -ItemType Directory -Path $Destination
        foreach ($file in $manifest.files) {
            $target = Join-Path $Destination $file.name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($file.name), $target)
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $file.sha256) { throw "Payload SHA-256 mismatch: $($file.name)." }
        }
        return $manifest
    } finally { $zip.Dispose() }
}

function Get-MedalRelease([string]$Tag = 'latest') {
    if ($Tag -ne 'latest') {
        $version = $Tag -creplace '^v', ''
        $null = Assert-MedalVersion $version
        $Tag = "v$version"
    }
    $endpoint = if ($Tag -eq 'latest') { 'latest' } else { "tags/$Tag" }
    # No caller-selected host/repository or token: never send private credentials.
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/Larson-Logan/medal-sync/releases/$endpoint" `
        -Headers @{ Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'; 'User-Agent'='MedalSync-Updater' } -TimeoutSec 30
    if ($release.draft -or $release.prerelease -or $release.tag_name -cnotmatch "^v$script:StableVersion$") { throw 'Expected a published stable release.' }
    if ($Tag -ne 'latest' -and $release.tag_name -cne $Tag) { throw 'Release tag mismatch.' }
    $version = $release.tag_name.Substring(1)
    $name = "medal-sync-$version-win-x64.zip"
    $assets = @($release.assets | Where-Object name -CEQ $name)
    if ($assets.Count -ne 1 -or $assets[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Release has no unique package with a GitHub SHA-256 digest.' }
    $url = "https://github.com/Larson-Logan/medal-sync/releases/download/$($release.tag_name)/$name"
    if ($assets[0].browser_download_url -cne $url -or $assets[0].size -gt 500MB -or $assets[0].size -lt 1) { throw 'Unexpected release asset URL or size.' }
    return [pscustomobject]@{ Version=$version; Name=$name; Url=$url; Sha256=$assets[0].digest.Substring(7); Size=$assets[0].size }
}

function Assert-MedalLocalPath([string]$StateRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($StateRoot) -or $StateRoot.StartsWith('\\')) { throw 'StateRoot must be an explicit absolute local path.' }
    $root = [IO.Path]::GetFullPath($StateRoot)
    if ([IO.Path]::TrimEndingDirectorySeparator($root) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetPathRoot($root))) { throw 'StateRoot cannot be a drive root.' }
    $cursor = $root
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation paths must not traverse junctions or symbolic links.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    foreach ($name in @('bin', 'updates', 'sessions')) {
        $path = Join-Path $root $name
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation directories must not be links.' }
    }
    if (Test-Path -LiteralPath (Join-Path $root 'bin/setup-layout.json')) { throw 'This is a native Windows installation. Use Windows setup or Installed apps to update or uninstall it.' }
    return $root
}

function Assert-MedalInstallPath([string]$StateRoot) {
    $root = Assert-MedalLocalPath $StateRoot
    foreach ($name in @('config.json', 'device.json', 'bin/medal-sync.exe', 'bin/medal-sync-launch.exe')) {
        $path = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Expected an existing configured installation: $name." }
    }
    return $root
}

function Invoke-MedalProcess([string]$Path, [string[]]$Arguments) {
    $output = & $Path @Arguments
    return [pscustomobject]@{ ExitCode=$LASTEXITCODE; Output=@($output) }
}

function Invoke-MedalBinary([string]$Path, [string[]]$Arguments) {
    $result = Invoke-MedalProcess $Path $Arguments
    if ($result.ExitCode -ne 0) { throw "Medal Sync check failed: $($Arguments -join ' ')." }
    return $result.Output
}

function Enter-MedalMaintenance {
    # Native setup holds the same object. While it exists the CLI refuses new library operations,
    # but still answers version and setup check.
    if ($script:Maintenance) { throw 'This updater already holds the setup lock.' }
    $created = $false
    $mutex = [Threading.Mutex]::new($false, $script:SetupMutexName, [ref]$created)
    if (-not $created) { $mutex.Dispose(); throw 'Medal Sync setup, uninstall or another updater is open. Close it before continuing.' }
    $script:Maintenance = $mutex
}

function Exit-MedalMaintenance {
    if ($script:Maintenance) { $script:Maintenance.Dispose(); $script:Maintenance = $null }
}

function Assert-MedalMaintenance {
    if (-not $script:Maintenance) { throw 'Hold the setup lock (Enter-MedalMaintenance) before changing programs.' }
}

function Enter-MedalInstanceLock {
    # The lock launch and finish hold. Take it only after Assert-MedalIdle: every CLI release reads a held
    # lock as a running supervisor. The setup lock, held first, keeps new sessions from starting in between.
    $mutex = [Threading.Mutex]::new($false, $script:InstanceMutexName)
    $locked = $false
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { $mutex.Dispose(); throw 'A Medal Sync session or another installer is running.' }
    return $mutex
}

function Exit-MedalInstanceLock($Mutex) {
    if ($Mutex) { $Mutex.ReleaseMutex(); $Mutex.Dispose() }
}

function Test-MedalStatusViewOpen {
    $mutex = $null
    try { return [Threading.Mutex]::TryOpenExisting($script:StatusViewMutexName, [ref]$mutex) }
    catch [UnauthorizedAccessException] { return $true }
    finally { if ($mutex) { $mutex.Dispose() } }
}

function Assert-MedalStopped([string]$Message) {
    $others = @(Get-Process -Name 'Medal', 'MedalEncoder', 'MedalRecorder', 'medal-sync' -ErrorAction SilentlyContinue | Where-Object { $_ })
    $launchers = @(Get-Process -Name 'medal-sync-launch' -ErrorAction SilentlyContinue | Where-Object { $_ })
    if ($others.Count -eq 0 -and $launchers.Count -eq 0) { return }
    # A lone launcher holding the view's mutex is the status view, which stays open after Medal and supervision stop.
    if ($others.Count -eq 0 -and $launchers.Count -eq 1 -and (Test-MedalStatusViewOpen)) { throw $script:StatusViewOpen }
    throw $Message
}

function Assert-MedalNoLocalSession([string]$Root, [string]$Message) {
    if (@(Get-ChildItem -LiteralPath (Join-Path $Root 'sessions') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0) { throw $Message }
}

function Invoke-MedalSetupCheck([string]$Root) {
    $result = Invoke-MedalProcess (Join-Path $Root 'bin/medal-sync.exe') @('setup', 'check', '--state-root', $Root)
    return [pscustomobject]@{ Ready=($result.ExitCode -eq 0); Message=(@($result.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' ') }
}

function Assert-MedalIdle([string]$Root, [string]$Action = 'updating') {
    # Hold the setup lock, not the instance lock. Every release since 1.0.0 answers setup check
    # while setup runs and checks what Windows setup checks: no Medal or supervisor, no retained
    # session login, and a Clean journal with no owner or session.
    Assert-MedalMaintenance
    Assert-MedalStopped "Quit Medal and wait for Ready before $Action."
    Assert-MedalNoLocalSession $Root "Unfinished local session evidence exists; recover it before $Action."
    $check = Invoke-MedalSetupCheck $Root
    if (-not $check.Ready) {
        $reason = if ($check.Message) { $check.Message } else { 'The readiness check failed.' }
        throw "Medal Sync is not Ready. $reason"
    }
}

function Assert-MedalNoInterruptedUpdate([string]$Root) {
    foreach ($receipt in @(Get-ChildItem -LiteralPath (Join-Path $Root 'updates') -Filter receipt.json -Recurse -ErrorAction SilentlyContinue)) {
        $prior = Get-Content -LiteralPath $receipt.FullName -Raw | ConvertFrom-Json
        if ($prior.state -notin @('complete', 'rolledBack')) { throw "Interrupted update requires recovery: $($receipt.FullName)." }
    }
}

function Restore-MedalStartup([string]$Root) {
    if (Test-Path -LiteralPath (Join-Path $Root 'startup-originals.json')) {
        & (Join-Path $PSScriptRoot 'set-medal-startup.ps1') -Restore -StateRoot $Root
    }
    # Refuse binary removal if any known entry still refers to this installation.
    $launcher = Join-Path $Root 'bin/medal-sync-launch.exe'
    foreach ($keyPath in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\Software\Classes\medal\shell\open\command')) {
        if (Test-Path -LiteralPath $keyPath) {
            $key = Get-Item -LiteralPath $keyPath
            foreach ($name in $key.GetValueNames()) {
                if (([string]$key.GetValue($name)).IndexOf($launcher, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'A startup entry still references this installation; restore it before uninstalling.' }
            }
        }
    }
    $shell = New-Object -ComObject WScript.Shell
    $folders = @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'), (Join-Path $env:APPDATA 'Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar'))
    foreach ($folder in $folders) {
        if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -Recurse) {
            if ($shell.CreateShortcut($file.FullName).TargetPath -ieq $launcher) { throw 'A shortcut still references this installation; restore it before uninstalling.' }
        }
    }
}

function Install-MedalFresh([string]$StateRoot, [string]$Staged, $Manifest) {
    $null = Assert-MedalVersion $Manifest.version
    $root = Assert-MedalLocalPath $StateRoot
    Assert-MedalMaintenance
    Assert-MedalNoInterruptedUpdate $root
    $configured = (Test-Path -LiteralPath (Join-Path $root 'config.json')) -and (Test-Path -LiteralPath (Join-Path $root 'device.json'))
    if (-not $configured -and ((Test-Path -LiteralPath (Join-Path $root 'config.json')) -or (Test-Path -LiteralPath (Join-Path $root 'device.json')))) { throw 'Partial configuration; recover installation before reinstalling.' }
    $present = @($script:Payload | Where-Object { Test-Path -LiteralPath (Join-Path $root "bin/$_") })
    if ($present.Count -gt 0) { throw 'Install requires absent companion programs. Use update for an existing installation.' }
    if (-not $configured -and @(Get-ChildItem -LiteralPath (Join-Path $root 'bin') -Force -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Install requires a new or empty bin directory.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $root 'sessions') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Unfinished local session evidence exists; recover before reinstalling.' }
    if ($configured) {
        $uninstalled = @(Get-ChildItem -LiteralPath (Join-Path $root 'updates') -Filter receipt.json -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object { $_.PSObject.Properties['operation'] -and $_.operation -eq 'uninstall' -and $_.state -eq 'complete' })
        if ($uninstalled.Count -eq 0) { throw 'Reinstalling retained state requires a completed uninstall receipt.' }
        foreach ($prior in $uninstalled) {
            if ((Assert-MedalVersion $Manifest.version) -lt (Assert-MedalVersion $prior.version)) { throw 'Reinstall cannot downgrade retained state.' }
        }
    }
    Assert-MedalStopped 'Quit Medal and Medal Sync before installing.'
    # Retained state is checked with the new CLI once it is copied; with no binary yet, nothing here can run a session.
    $lock = if ($configured) { $null } else { Enter-MedalInstanceLock }
    $transaction = Join-Path $root ('updates/' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $transaction -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'bin') -Force
    $receiptPath = Join-Path $transaction 'receipt.json'
    $receipt = @{ format=1; state='installing'; version=$Manifest.version; operation='install' }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    try {
        foreach ($name in $script:Payload) { Copy-Item -LiteralPath (Join-Path $Staged $name) -Destination (Join-Path $root "bin/$name") }
        foreach ($file in $Manifest.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $root "bin/$($file.name)")).Hash -ine $file.sha256) { throw 'Installed file failed verification.' }
        }
        if ($configured) {
            $null = Assert-MedalInstallPath $root
            Assert-MedalIdle $root 'installing'
            $lock = Enter-MedalInstanceLock
        }
        $version = Invoke-MedalBinary (Join-Path $root 'bin/medal-sync.exe') @('version')
        if (($version -join '') -notlike "medal-sync $($Manifest.version);*") { throw 'Installed CLI smoke test failed.' }
        $receipt.state = 'complete'
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    } catch {
        $failure = $_
        foreach ($name in $script:Payload) {
            $target = Join-Path $root "bin/$name"
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        }
        $receipt.state = 'rolledBack'
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
        throw $failure
    } finally { Exit-MedalInstanceLock $lock }
    return $receiptPath
}

function Uninstall-MedalRelease([string]$StateRoot) {
    $root = Assert-MedalLocalPath $StateRoot
    Assert-MedalMaintenance
    Assert-MedalNoInterruptedUpdate $root
    Assert-MedalStopped 'Quit Medal and wait for Ready before uninstalling.'
    $present = @($script:Payload | Where-Object { Test-Path -LiteralPath (Join-Path $root "bin/$_") })
    if ($present.Count -eq 0) { return 'Companion programs are already absent. Retained state was preserved.' }
    foreach ($name in $present) {
        if ((Get-Item -LiteralPath (Join-Path $root "bin/$name") -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installed payload must not be a link.' }
    }
    $version = Invoke-MedalBinary (Join-Path $root 'bin/medal-sync.exe') @('version')
    if (($version -join '') -notmatch '^medal-sync ([0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?);') { throw 'Cannot identify installed version.' }
    $installed = $Matches[1]
    $null = Assert-MedalVersion $installed
    if (Test-Path -LiteralPath (Join-Path $root 'config.json')) {
        $null = Assert-MedalInstallPath $root
        Assert-MedalIdle $root 'uninstalling'
    } elseif (Test-Path -LiteralPath (Join-Path $root 'device.json')) { throw 'Partial configuration; recover installation before uninstalling.' }
    Assert-MedalNoLocalSession $root 'Unfinished local session evidence exists; recover before uninstalling.'
    $lock = Enter-MedalInstanceLock
    try {
        Restore-MedalStartup $root
        $transaction = Join-Path $root ('updates/' + [guid]::NewGuid().ToString('N'))
        $backup = Join-Path $transaction 'previous'
        $null = New-Item -ItemType Directory -Path $backup -Force
        foreach ($name in $present) {
            $original = Join-Path $root "bin/$name"
            Copy-Item -LiteralPath $original -Destination $backup
            if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backup $name)).Hash) { throw 'Uninstall backup verification failed.' }
        }
        $receiptPath = Join-Path $transaction 'receipt.json'
        $receipt = @{ format=1; state='uninstalling'; operation='uninstall'; version=$installed; files=$present }
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
        try {
            foreach ($name in $present) { Remove-Item -LiteralPath (Join-Path $root "bin/$name") -Force }
            $receipt.state = 'complete'
            $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
        } catch {
            $failure = $_
            foreach ($name in $present) { Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $root "bin/$name") -Force }
            $receipt.state = 'rolledBack'
            $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
            throw $failure
        }
    } finally { Exit-MedalInstanceLock $lock }
    return $receiptPath
}

function Install-MedalRelease([string]$Root, [string]$Staged, $Manifest) {
    $null = Assert-MedalVersion $Manifest.version
    $Root = Assert-MedalInstallPath $Root
    $updates = Join-Path $Root 'updates'
    Assert-MedalNoInterruptedUpdate $Root
    # The installed CLI answers this check; then the same lock as launch/finish is held to the end.
    Assert-MedalIdle $Root
    $lock = Enter-MedalInstanceLock
    try { return Install-MedalReleaseLocked $Root $Staged $Manifest $updates }
    finally { Exit-MedalInstanceLock $lock }
}

function Install-MedalReleaseLocked([string]$Root, [string]$Staged, $Manifest, [string]$updates) {
    $current = Invoke-MedalBinary (Join-Path $Root 'bin/medal-sync.exe') @('version')
    if (($current -join '') -notmatch '^medal-sync ([0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?);') { throw 'Cannot identify installed version.' }
    $installed = Assert-MedalVersion $Matches[1]
    if ((Assert-MedalVersion $Manifest.version) -le $installed) { throw 'The package must be newer than the installed version.' }
    # Never overwrite a helper script that is currently executing from bin.
    foreach ($name in $script:Payload) {
        $target = Join-Path $Root "bin/$name"
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installed payload must not be a link.' }
    }
    $transaction = Join-Path $updates ([guid]::NewGuid().ToString('N'))
    $backup = Join-Path $transaction 'previous'
    $null = New-Item -ItemType Directory -Path $backup -Force
    $files = @($script:Payload | ForEach-Object { [ordered]@{ name=$_; existed=(Test-Path -LiteralPath (Join-Path $Root "bin/$_")) } })
    foreach ($file in $files) {
        if ($file.existed) {
            $original = Join-Path $Root "bin/$($file.name)"
            Copy-Item -LiteralPath $original -Destination $backup
            if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath (Join-Path $backup $file.name)).Hash) { throw 'Update backup verification failed.' }
        }
    }
    $receiptPath = Join-Path $transaction 'receipt.json'
    $receipt = [ordered]@{ format=1; state='applying'; version=$Manifest.version; previousVersion=$installed.ToString(); files=$files }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    try {
        # Recheck after all backup work and immediately before replacing binaries. Both locks are held,
        # so no session can start here; the CLI would now read the held lock as a supervisor.
        Assert-MedalStopped 'Quit Medal and wait for Ready before updating.'
        Assert-MedalNoLocalSession $Root 'Unfinished local session evidence exists; recover it before updating.'
        foreach ($name in $script:Payload) { Copy-Item -LiteralPath (Join-Path $Staged $name) -Destination (Join-Path $Root "bin/$name") -Force }
        foreach ($file in $Manifest.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $Root "bin/$($file.name)") -Algorithm SHA256).Hash -ine $file.sha256) { throw 'Installed file failed verification.' }
        }
        $version = Invoke-MedalBinary (Join-Path $Root 'bin/medal-sync.exe') @('version')
        if (($version -join '') -notlike "medal-sync $($Manifest.version);*") { throw 'Installed CLI smoke test failed.' }
        $receipt.state = 'complete'
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    } catch {
        $failure = $_
        foreach ($file in $files) {
            $target = Join-Path $Root "bin/$($file.name)"
            if ($file.existed) { Copy-Item -LiteralPath (Join-Path $backup $file.name) -Destination $target -Force }
            elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        }
        $receipt.state = 'rolledBack'
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
        throw $failure
    }
    return $receiptPath
}

Export-ModuleMember -Function Get-MedalPayloadNames, Enter-MedalMaintenance, Exit-MedalMaintenance, Assert-MedalVersion, Assert-MedalBuildVersion, Get-MedalBuildVersion, Read-MedalReleasePackage, Get-MedalRelease, Assert-MedalInstallPath, Assert-MedalLocalPath, Install-MedalRelease, Install-MedalFresh, Uninstall-MedalRelease
