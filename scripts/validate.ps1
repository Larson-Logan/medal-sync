<#
.SYNOPSIS
Runs the standalone validation suite. With no arguments every part runs.
.DESCRIPTION
CI runs the parts as separate parallel jobs; contributors normally run them all.
  Unit        .NET unit tests
  Desktop     launcher build, native desktop checks and integration build
  Scripts     Node, Python and PowerShell helper tests
  Repository  source hygiene, public inventory and local Markdown links
#>
[CmdletBinding()]
param(
    [ValidateSet('Unit', 'Desktop', 'Scripts', 'Repository')]
    [string[]]$Part = @('Unit', 'Desktop', 'Scripts', 'Repository')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
Push-Location $repo
try {
    if ('Unit' -in $Part) {
        Invoke-Checked dotnet @('restore','tests/tools/medal-sync/MedalSync.Tests.csproj','--locked-mode')
        # global.json opts dotnet test into Microsoft.Testing.Platform mode; it forwards unknown flags such as --nologo to the test app.
        Invoke-Checked dotnet @('test','--project','tests/tools/medal-sync/MedalSync.Tests.csproj','-c','Release','--no-restore')
    }
    if ('Desktop' -in $Part) {
        Invoke-Checked dotnet @('restore','tools/medal-sync/MedalSync.Launch/MedalSync.Launch.csproj','--locked-mode')
        Invoke-Checked dotnet @('build','tools/medal-sync/MedalSync.Launch/MedalSync.Launch.csproj','-c','Release','--no-restore','--nologo')
        Invoke-Checked dotnet @('restore','tests/desktop/MedalSync.Desktop.Tests.csproj','--locked-mode')
        Invoke-Checked dotnet @('run','--project','tests/desktop/MedalSync.Desktop.Tests.csproj','-c','Release','--no-restore')
        Invoke-Checked dotnet @('restore','tests/integration/MedalSync.Integration.csproj','--locked-mode')
        Invoke-Checked dotnet @('build','tests/integration/MedalSync.Integration.csproj','-c','Release','--no-restore','--nologo')
    }
    if ('Scripts' -in $Part) {
        $nodeTests = @(Get-ChildItem tests/scripts/storage/medal-sync -Filter '*.test.mjs' -File | ForEach-Object FullName)
        Invoke-Checked node (@('--test') + $nodeTests)
        Invoke-Checked python @('tests/scripts/storage/medal-sync/recycle-exclusion.test.py')
        Invoke-Checked pwsh @('-NoProfile','-File','tests/scripts/storage/medal-sync/update-release.test.ps1')
    }
    if ('Repository' -in $Part) {
        Invoke-Checked python @('scripts/check-repository.py')
        Invoke-Checked python @('scripts/update-source-manifest.py', '--check')
        Invoke-Checked python @('tests/test_source_hygiene.py')
        Invoke-Checked python @('tests/test_source_manifest.py')
        Invoke-Checked python @('tests/test_branch_cleanup.py')
        Invoke-Checked python @('tests/test_release_pipeline.py')
        $docs = @(& python scripts/check-repository.py --list-docs)
        if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate source documentation.' }
        Invoke-Checked node (@('scripts/check-doc-links.mjs') + $docs)
    }
    Write-Output "PASS: standalone Medal Sync validation ($($Part -join ', '))"
} finally { Pop-Location }
