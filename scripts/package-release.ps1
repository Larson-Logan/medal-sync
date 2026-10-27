<# .SYNOPSIS
Builds a versioned Windows release ZIP from an explicit payload allowlist.
#>
[CmdletBinding()]
param([switch]$SkipBuild)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'scripts/storage/medal-sync/MedalSync.Update.psm1') -Force
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.VersionPrefix
$null = Assert-MedalVersion $version
if (-not $SkipBuild) {
    & (Join-Path $root 'scripts/storage/medal-sync/publish-medal-sync.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
$output = Join-Path $root '.medal-sync-dist'
$stage = Join-Path $output ('package-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
try {
    $sdkRoot = Split-Path -Parent (Get-Command dotnet -CommandType Application).Source
    foreach ($name in Get-MedalPayloadNames) {
        $source = if ($name.EndsWith('.exe')) { Join-Path $output $name }
        elseif ($name -eq 'DOTNET-LICENSE.txt') { Join-Path $sdkRoot 'LICENSE.txt' }
        elseif ($name -eq 'DOTNET-NOTICES.txt') { Join-Path $sdkRoot 'ThirdPartyNotices.txt' }
        elseif ($name -in @('LICENSE', 'THIRD_PARTY.md')) { Join-Path $root $name }
        else { Join-Path $root "scripts/storage/medal-sync/$name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name)
    }
    $cliVersion = & (Join-Path $stage 'medal-sync.exe') version
    if ($LASTEXITCODE -ne 0 -or ($cliVersion -join '') -notlike "medal-sync $version;*") { throw 'Published binary version differs from Directory.Build.props.' }
    $launcherVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'medal-sync-launch.exe')).ProductVersion.Split('+')[0]
    if ($launcherVersion -cne $version) { throw 'Launcher version differs from Directory.Build.props.' }
    $manifest = [ordered]@{ format=1; version=$version; runtime='win-x64'; files=@(Get-MedalPayloadNames | ForEach-Object {
        [ordered]@{ name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
    }) }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'release.json') -Encoding utf8NoBOM
    $archive = Join-Path $output "medal-sync-$version-win-x64.zip"
    if (Test-Path -LiteralPath $archive) { throw 'Release archive already exists. Move the old archive aside before rebuilding.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    $null = Read-MedalReleasePackage $archive $hash (Join-Path $stage 'verified')
    Write-Output "Packaged $archive"
    Write-Output "SHA-256 $hash"
} finally {
    if ([IO.Path]::GetDirectoryName($stage) -ne $output) { throw 'Unexpected package staging path.' }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
