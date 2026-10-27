<#
.SYNOPSIS
Adds the clip exclusion to explicitly selected Syncthing .stignore files.
.DESCRIPTION
Preview by default. -Apply backs up all originals outside the synced folders,
places the rule first (before any negation), uses LF, and preserves hidden file
attributes. If a later write fails, earlier writes are restored. No Syncthing
process, API credential, clip or startup entry is touched. Back up a server-side
ignore under /root/backups as well before passing its UNC path; see the runbook.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$IgnoreFile,
    [string]$Pattern = '/Videos/Clips',
    [string]$BackupRoot = (Join-Path $env:USERPROFILE '.medal-sync/Backups'),
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Pattern -notmatch '^/(?!/)[^\\:*?"<>|\r\n]+$' -or @($Pattern.Substring(1).Split('/') | Where-Object { $_ -in '', '.', '..' }).Count) {
    throw 'Use one rooted folder pattern such as /Videos/Clips; no wildcards or relative segments.'
}
$backupBase = [IO.Path]::GetFullPath($BackupRoot)
if ($backupBase.StartsWith('\\')) { throw 'Backups must be on this PC outside the synced folders.' }
$planned = @()
foreach ($file in ($IgnoreFile | Select-Object -Unique)) {
    $path = [IO.Path]::GetFullPath($file)
    if ([IO.Path]::GetFileName($path) -ne '.stignore' -or -not [IO.File]::Exists($path)) { throw "Expected an existing .stignore: $path" }
    $ancestor = Get-Item -LiteralPath $path -Force
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Ignore path crosses a link: $path" }
        $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
    $parent = [IO.Path]::GetDirectoryName($path).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($backupBase.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or $backupBase -eq $parent.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "Backup directory is inside a selected synced folder: $backupBase"
    }
    $original = [IO.File]::ReadAllBytes($path)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($original).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
    $lines = @($text.Split("`n") | Where-Object { $_.Trim() -ne $Pattern })
    $desired = [Text.UTF8Encoding]::new($false).GetBytes($Pattern + "`n" + (($lines -join "`n").TrimEnd("`n")) + "`n")
    $changed = [Convert]::ToBase64String($original) -ne [Convert]::ToBase64String($desired)
    $planned += [pscustomobject]@{ Path=$path; Original=$original; Desired=$desired; Changed=$changed }
    Write-Output "$(if ($changed) {'Update'} else {'Already correct'}): $path; first rule $Pattern"
}
if (-not $Apply) { Write-Output 'Preview only; nothing was changed.'; return }
$changes = @($planned | Where-Object Changed)
if (-not $changes.Count) { Write-Output 'All selected ignore files already match; no backups or writes needed.'; return }
$backup = Join-Path $backupBase ('stignore-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$manifest = @()
for ($index=0; $index -lt $changes.Count; $index++) {
    $entry = $changes[$index]
    $saved = Join-Path $backup "$index.stignore"
    [IO.File]::WriteAllBytes($saved, $entry.Original)
    $manifest += [pscustomobject]@{ path=$entry.Path; backup=$saved; beforeSha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($entry.Original)); afterSha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($entry.Desired)) }
}
$manifest | ConvertTo-Json -Depth 4 -AsArray | Set-Content -LiteralPath (Join-Path $backup 'files.json') -Encoding utf8NoBOM
function Write-ExistingFile([string]$Path, [byte[]]$Bytes, [byte[]]$Expected) {
    # Open an existing file: FileMode.Create fails on hidden Windows files.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $current = [byte[]]::new($stream.Length)
        $stream.ReadExactly($current)
        if ([Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($Expected)) { throw "Ignore file changed after preview: $Path" }
        try {
            $stream.Position = 0
            $stream.Write($Bytes)
            $stream.SetLength($Bytes.Length)
            $stream.Flush($true)
        } catch {
            # We still hold the exclusive stream: restore even a partial write.
            $stream.Position = 0
            $stream.Write($Expected)
            $stream.SetLength($Expected.Length)
            $stream.Flush($true)
            throw
        }
    } finally { $stream.Dispose() }
}
$written = @()
try {
    foreach ($entry in $changes) {
        Write-ExistingFile $entry.Path $entry.Desired $entry.Original
        $written += $entry
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($entry.Path)) -ne [Convert]::ToBase64String($entry.Desired)) { throw "Readback failed: $($entry.Path)" }
    }
} catch {
    $failure = $_
    foreach ($entry in $written) {
        try {
            $current = [IO.File]::ReadAllBytes($entry.Path)
            # Never overwrite a concurrent edit during rollback.
            if ([Convert]::ToBase64String($current) -eq [Convert]::ToBase64String($entry.Desired)) {
                Write-ExistingFile $entry.Path $entry.Original $current
            }
        } catch { Write-Warning "Could not restore $($entry.Path); original retained in $backup" }
    }
    throw $failure
}
Write-Output "Applied. Originals and hashes: $backup"
Write-Output 'Rescan each peer and prove a synthetic Clips file does not sync before moving any media.'
