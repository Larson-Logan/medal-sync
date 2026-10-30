# Synthetic tests only: fake payloads, fake process/status probes, private temp root.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$module = Import-Module (Join-Path $repo 'scripts/storage/medal-sync/MedalSync.Update.psm1') -Force -PassThru
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('medal-release-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$script:passed = 0
function Assert-True($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test-Case([string]$Name, [scriptblock]$Action) { & $Action; $script:passed++; Write-Output "PASS: $Name" }
function Refuses([scriptblock]$Action, [string]$Pattern) {
    try { & $Action; throw 'Expected refusal did not occur.' } catch { if ($_.Exception.Message -notlike $Pattern) { throw } }
}
function New-Package([string]$Name = 'valid') {
    $payload = Join-Path $scratch $Name
    $null = New-Item -ItemType Directory -Path $payload
    foreach ($name in Get-MedalPayloadNames) { Set-Content -LiteralPath (Join-Path $payload $name) -Value '0.2.0' -NoNewline }
    $manifest = @{ format=1; version='0.2.0'; runtime='win-x64'; files=@(Get-MedalPayloadNames | ForEach-Object {
        @{ name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $payload $_)).Hash.ToLowerInvariant() }
    }) }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'release.json')
    return @{ path=$payload; manifest=$manifest }
}
function Zip-Package($Package, [string]$Name) {
    $zip = Join-Path $scratch "$Name.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($Package.path, $zip)
    return @{ path=$zip; hash=(Get-FileHash -LiteralPath $zip).Hash }
}
function New-Install([string]$Name) {
    $root = Join-Path $scratch $Name
    $null = New-Item -ItemType Directory -Path (Join-Path $root 'bin')
    foreach ($name in Get-MedalPayloadNames) { Set-Content -LiteralPath (Join-Path $root "bin/$name") -Value '0.1.0' -NoNewline }
    foreach ($name in @('config.json', 'device.json')) { Set-Content -LiteralPath (Join-Path $root $name) -Value '{"sentinel":"preserve"}' -NoNewline }
    return $root
}
try {
    # These module-local probes prevent any live process or library access.
    & $module {
        function script:Get-Process { return $null }
        function script:Restore-MedalStartup { $script:StartupRestored = $true }
        $script:TestJournal = '{"format":1,"state":"CLEAN","owner":null,"sessionId":null}'
        $script:TestSmokeFailure = $false
        function script:Invoke-MedalBinary([string]$Path, [string[]]$Arguments) {
            if ($Arguments[0] -eq 'status') { return $script:TestJournal }
            $version = Get-Content -LiteralPath $Path -Raw
            if ($script:TestSmokeFailure -and $version -eq '0.2.0') { throw 'Synthetic smoke failure.' }
            return "medal-sync $version; synthetic"
        }
    }
    $package = New-Package
    $zip = Zip-Package $package 'valid'
    Test-Case 'Normalize bare release versions to exact stable GitHub tags' {
        & $module {
            function script:Invoke-RestMethod {
                param($Uri, $Headers, $TimeoutSec)
                if ($Uri -cne 'https://api.github.com/repos/Larson-Logan/medal-sync/releases/tags/v0.2.0') { throw 'Unexpected release endpoint.' }
                return @{ draft=$false; prerelease=$false; tag_name='v0.2.0'; assets=@(@{
                    name='medal-sync-0.2.0-win-x64.zip'; digest=('sha256:' + ('a' * 64)); size=1024
                    browser_download_url='https://github.com/Larson-Logan/medal-sync/releases/download/v0.2.0/medal-sync-0.2.0-win-x64.zip'
                }) }
            }
        }
        try {
            foreach ($tag in @('0.2.0','v0.2.0')) { Assert-True ((Get-MedalRelease $tag).Version -eq '0.2.0') 'Wrong release version.' }
        } finally { & $module { Remove-Item Function:script:Invoke-RestMethod } }
    }
    Test-Case 'Build version adds the hotfix counter to VersionPrefix' {
        $props = Join-Path $scratch 'props'
        $null = New-Item -ItemType Directory -Path $props
        @'
<Project>
  <PropertyGroup>
    <VersionPrefix>1.4.2</VersionPrefix>
    <MedalReleaseRevision Condition="'$(MedalReleaseRevision)' == ''">00</MedalReleaseRevision>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $props 'Directory.Build.props')
        $saved = $env:MedalReleaseRevision
        try {
            $env:MedalReleaseRevision = $null
            Assert-True ((Get-MedalBuildVersion $props) -ceq '1.4.2.00') 'Local builds must use counter 00.'
            $env:MedalReleaseRevision = '07'
            Assert-True ((Get-MedalBuildVersion $props) -ceq '1.4.2.07') 'Release builds must use the workflow counter.'
            $env:MedalReleaseRevision = 'none'
            Assert-True ((Get-MedalBuildVersion $props) -ceq '1.4.2') 'The bridge release must use three parts.'
            $env:MedalReleaseRevision = '7'
            Refuses { Get-MedalBuildVersion $props } '*two or more digits*'
        } finally { $env:MedalReleaseRevision = $saved }
    }
    Test-Case 'Keep the hotfix counter when normalizing release tags' {
        & $module {
            function script:Invoke-RestMethod {
                param($Uri, $Headers, $TimeoutSec)
                if ($Uri -cne 'https://api.github.com/repos/Larson-Logan/medal-sync/releases/tags/v0.2.0.03') { throw 'Unexpected release endpoint.' }
                return @{ draft=$false; prerelease=$false; tag_name='v0.2.0.03'; assets=@(@{
                    name='medal-sync-0.2.0.03-win-x64.zip'; digest=('sha256:' + ('a' * 64)); size=1024
                    browser_download_url='https://github.com/Larson-Logan/medal-sync/releases/download/v0.2.0.03/medal-sync-0.2.0.03-win-x64.zip'
                }) }
            }
        }
        try {
            foreach ($tag in @('0.2.0.03','v0.2.0.03')) { Assert-True ((Get-MedalRelease $tag).Version -ceq '0.2.0.03') 'Wrong release version.' }
        } finally { & $module { Remove-Item Function:script:Invoke-RestMethod } }
    }
    Test-Case 'Portable lifecycle refuses a native Windows layout before changing programs' {
        $root = New-Install 'native-layout'
        '{}' | Set-Content -LiteralPath (Join-Path $root 'bin/setup-layout.json')
        Refuses { Install-MedalRelease $root $package.path $package.manifest } '*native Windows installation*'
        Refuses { Install-MedalFresh $root $package.path $package.manifest } '*native Windows installation*'
        Refuses { Uninstall-MedalRelease $root } '*native Windows installation*'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'bin/medal-sync.exe') -Raw) -eq '0.1.0') 'Changed a native program.'
    }
    Test-Case 'Accept verified package' {
        $read = Read-MedalReleasePackage $zip.path $zip.hash (Join-Path $scratch 'extracted')
        Assert-True ($read.version -eq '0.2.0') 'Wrong version.'
    }
    Test-Case 'Reject wrong archive digest before extraction' {
        Refuses { Read-MedalReleasePackage $zip.path ('0' * 64) (Join-Path $scratch 'bad-hash') } '*archive SHA-256 mismatch*'
        Assert-True (-not (Test-Path (Join-Path $scratch 'bad-hash'))) 'Created destination before verifying.'
    }
    Test-Case 'Reject changed payload with matching archive digest' {
        $bad = New-Package 'tampered'
        Set-Content -LiteralPath (Join-Path $bad.path 'medal-sync.exe') -Value 'tampered'
        $badZip = Zip-Package $bad 'tampered'
        Refuses { Read-MedalReleasePackage $badZip.path $badZip.hash (Join-Path $scratch 'tampered-out') } '*Payload SHA-256 mismatch*'
    }
    Test-Case 'Reject path traversal and duplicate entries' {
        foreach ($name in @('../escape', 'release.json')) {
            $path = Join-Path $scratch ([guid]::NewGuid().ToString('N') + '.zip')
            Copy-Item -LiteralPath $zip.path -Destination $path
            $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Update)
            try { $null = $archive.CreateEntry($name) } finally { $archive.Dispose() }
            Refuses { Read-MedalReleasePackage $path (Get-FileHash -LiteralPath $path).Hash (Join-Path $scratch ([guid]::NewGuid().ToString('N'))) } '*archive entries*'
        }
    }
    Test-Case 'Reject prerelease versions, malformed versions and missing files' {
        foreach ($value in @('v1.0.0', '1.0.0-beta', '01.0.0', '../1.0.0', '1.0.0.1', '1.0.0.001', '1.0.0.00.1', 'v1.0.0.01')) { Refuses { Assert-MedalVersion $value } '*stable version*' }
        foreach ($value in @('1.0.0', '1.0.0.00', '1.0.0.01', '1.0.0.10', '1.0.0.100')) { $null = Assert-MedalVersion $value }
        Assert-True ((Assert-MedalVersion '1.0.0.10') -gt (Assert-MedalVersion '1.0.0.09')) 'Hotfix counter must compare numerically.'
        Assert-True ((Assert-MedalVersion '1.0.0.00') -gt (Assert-MedalVersion '1.0.0')) 'The first hotfix-counted release must be newer than its three-part version.'
        $bad = New-Package 'missing'
        Remove-Item -LiteralPath (Join-Path $bad.path 'medal-sync-launch.exe')
        $badZip = Zip-Package $bad 'missing'
        Refuses { Read-MedalReleasePackage $badZip.path $badZip.hash (Join-Path $scratch 'missing-out') } '*archive entries*'
    }
    Test-Case 'Install complete payload and retain original configuration and binaries' {
        $root = New-Install 'install'
        $null = Assert-MedalInstallPath $root
        $receiptPath = Install-MedalRelease $root $package.path $package.manifest
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        Assert-True ($receipt.state -eq 'complete') 'Update incomplete.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'bin/medal-sync.exe') -Raw) -eq '0.2.0') 'No new CLI.'
        Assert-True ((Get-Content -LiteralPath (Join-Path (Split-Path $receiptPath) 'previous/medal-sync.exe') -Raw) -eq '0.1.0') 'No old CLI.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'config.json') -Raw) -eq '{"sentinel":"preserve"}') 'Changed config.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'device.json') -Raw) -eq '{"sentinel":"preserve"}') 'Changed identity.'
        Refuses { Install-MedalRelease $root $package.path $package.manifest } '*must be newer*'
    }
    Test-Case 'Reject all non-Ready and unknown ownership states' {
        $root = New-Install 'busy'
        foreach ($state in @('ACTIVE', 'DRAINING', 'PUBLISHING', 'RECOVERY_REQUIRED', 'STARTING', 'unknown')) {
            & $module { param($State) $script:TestJournal = "{`"format`":1,`"state`":`"$State`",`"owner`":null,`"sessionId`":null}" } $state
            Refuses { Install-MedalRelease $root $package.path $package.manifest } '*must be Ready*'
        }
        & $module { $script:TestJournal = '{"format":1,"state":"CLEAN","owner":null,"sessionId":null}' }
        $null = New-Item -ItemType Directory -Path (Join-Path $root 'sessions')
        '{}' | Set-Content -LiteralPath (Join-Path $root 'sessions/pending.json')
        Refuses { Install-MedalRelease $root $package.path $package.manifest } '*Unfinished local session*'
    }
    Test-Case 'Rollback both binaries after partial copy failure' {
        $root = New-Install 'copy-failure'
        $broken = New-Package 'broken'
        Remove-Item -LiteralPath (Join-Path $broken.path 'medal-sync-launch.exe')
        Refuses { Install-MedalRelease $root $broken.path $broken.manifest } '*does not exist*'
        foreach ($name in Get-MedalPayloadNames) { Assert-True ((Get-Content -LiteralPath (Join-Path $root "bin/$name") -Raw) -eq '0.1.0') 'Rollback missed a file.' }
    }
    Test-Case 'Rollback after failed smoke check' {
        $root = New-Install 'smoke-failure'
        & $module { $script:TestSmokeFailure = $true }
        Refuses { Install-MedalRelease $root $package.path $package.manifest } '*Synthetic smoke failure*'
        & $module { $script:TestSmokeFailure = $false }
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'bin/medal-sync.exe') -Raw) -eq '0.1.0') 'CLI not rolled back.'
        $receipt = Get-ChildItem -LiteralPath (Join-Path $root 'updates') -Recurse -Filter receipt.json
        Assert-True ((Get-Content -LiteralPath $receipt.FullName -Raw | ConvertFrom-Json).state -eq 'rolledBack') 'Rollback not recorded.'
    }
    Test-Case 'Refuse interrupted update evidence' {
        $root = New-Install 'interrupted'
        $null = New-Item -ItemType Directory -Path (Join-Path $root 'updates/prior')
        '{"state":"applying"}' | Set-Content -LiteralPath (Join-Path $root 'updates/prior/receipt.json')
        Refuses { Install-MedalRelease $root $package.path $package.manifest } '*Interrupted update requires recovery*'
    }
    Test-Case 'Reject unconfigured install and installation junction' {
        Refuses { Assert-MedalInstallPath (Join-Path $scratch 'absent') } '*existing configured installation*'
        Refuses { Assert-MedalInstallPath 'relative' } '*absolute local path*'
        if ($IsWindows) {
            $root = New-Install 'junction-target'
            $link = Join-Path $scratch 'junction'
            $null = New-Item -ItemType Junction -Path $link -Target $root
            Refuses { Assert-MedalInstallPath $link } '*junctions or symbolic links*'
            Remove-Item -LiteralPath $link -Force
        }
    }
    Test-Case 'Fresh install is usable without creating configuration or device identity' {
        $root = Join-Path $scratch 'fresh'
        $receiptPath = Install-MedalFresh $root $package.path $package.manifest
        Assert-True ((Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json).state -eq 'complete') 'Install incomplete.'
        Assert-True (Test-Path -LiteralPath (Join-Path $root 'bin/medal-sync-launch.exe')) 'No launcher.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'config.json'))) 'Created configuration.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'device.json'))) 'Created device identity.'
        Refuses { Install-MedalFresh $root $package.path $package.manifest } '*absent companion programs*'
        Refuses { Install-MedalFresh (New-Install 'configured-fresh') $package.path $package.manifest } '*absent companion programs*'
    }
    Test-Case 'Fresh install rolls back failed smoke check' {
        $root = Join-Path $scratch 'fresh-failure'
        & $module { $script:TestSmokeFailure = $true }
        Refuses { Install-MedalFresh $root $package.path $package.manifest } '*Synthetic smoke failure*'
        & $module { $script:TestSmokeFailure = $false }
        foreach ($name in Get-MedalPayloadNames) { Assert-True (-not (Test-Path -LiteralPath (Join-Path $root "bin/$name"))) 'Partial installation retained.' }
    }
    Test-Case 'Uninstall restores startup and retains state, dependencies and original programs' {
        $root = New-Install 'uninstall'
        Set-Content -LiteralPath (Join-Path $root 'bin/rclone.exe') -Value 'dependency'
        $receiptPath = Uninstall-MedalRelease $root
        Assert-True (& $module { $script:StartupRestored }) 'Startup not restored.'
        Assert-True ((Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json).operation -eq 'uninstall') 'No uninstall receipt.'
        foreach ($name in Get-MedalPayloadNames) {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $root "bin/$name"))) 'Installed payload remains.'
            Assert-True (Test-Path -LiteralPath (Join-Path (Split-Path $receiptPath) "previous/$name")) 'Payload backup missing.'
        }
        Assert-True (Test-Path -LiteralPath (Join-Path $root 'bin/rclone.exe')) 'Deleted dependency.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'device.json') -Raw) -eq '{"sentinel":"preserve"}') 'Deleted identity.'
        $null = Uninstall-MedalRelease $root
        $null = Install-MedalFresh $root $package.path $package.manifest
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'bin/medal-sync.exe') -Raw) -eq '0.2.0') 'Reinstall failed.'
        Assert-True ((Get-Content -LiteralPath (Join-Path $root 'device.json') -Raw) -eq '{"sentinel":"preserve"}') 'Reinstall changed identity.'
    }
    Test-Case 'Uninstall refuses pending sessions and remaining startup references' {
        $root = New-Install 'uninstall-busy'
        & $module { $script:TestJournal = '{"format":1,"state":"ACTIVE","owner":null,"sessionId":null}' }
        Refuses { Uninstall-MedalRelease $root } '*must be Ready*'
        & $module {
            $script:TestJournal = '{"format":1,"state":"CLEAN","owner":null,"sessionId":null}'
            function script:Restore-MedalStartup { throw 'Synthetic remaining startup reference.' }
        }
        Refuses { Uninstall-MedalRelease $root } '*remaining startup reference*'
        Assert-True (Test-Path -LiteralPath (Join-Path $root 'bin/medal-sync.exe')) 'Removed binary with startup reference.'
        & $module { function script:Restore-MedalStartup { $script:StartupRestored = $true } }
    }
    Test-Case 'Installer and uninstaller refuse active processes' {
        & $module { function script:Get-Process { return 'synthetic active process' } }
        Refuses { Install-MedalFresh (Join-Path $scratch 'active-fresh') $package.path $package.manifest } '*Quit Medal*'
        Refuses { Uninstall-MedalRelease (New-Install 'active-uninstall') } '*Quit Medal*'
        & $module { function script:Get-Process { return $null } }
    }
    Write-Output "PASS: $script:passed release/updater tests"
} finally {
    if ([IO.Path]::GetDirectoryName($scratch) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) { throw 'Unexpected test scratch path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
