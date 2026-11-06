<# .SYNOPSIS
Replaces unsigned build outputs with the copies SignPath signed.

.DESCRIPTION
Used only by signed release builds (see docs/code-signing.md). Each named file
must exist in both folders, and its signed copy must carry a valid, timestamped
Authenticode signature before it replaces the unsigned file. Signing changes a
file's bytes, so -RefreshChecksum rewrites the file's SHA-256 sidecar.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SignedDirectory,
    [Parameter(Mandatory)][string[]]$Name,
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) '.medal-sync-dist'),
    [switch]$RefreshChecksum
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Authenticode signatures are checked on Windows only.' }
foreach ($file in $Name) {
    if ($file -cne [IO.Path]::GetFileName($file)) { throw "Expected a file name, not a path: $file" }
    $signed = Join-Path $SignedDirectory $file
    $target = Join-Path $Destination $file
    if (-not (Test-Path -LiteralPath $signed -PathType Leaf)) { throw "SignPath returned no signed copy of $file." }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "There is no unsigned $file to replace." }
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if ($signature.Status -ne 'Valid') { throw "The signed copy of $file has signature status $($signature.Status): $($signature.StatusMessage)" }
    # Without a timestamp the signature stops being valid when the certificate expires.
    if (-not $signature.TimeStamperCertificate) { throw "The signed copy of $file has no timestamp." }
    Copy-Item -LiteralPath $signed -Destination $target -Force
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($RefreshChecksum) { "$hash  $file" | Set-Content -LiteralPath "$target.sha256" -Encoding ascii }
    Write-Output "Signed by $($signature.SignerCertificate.Subject): $file (SHA-256 $hash)"
}
