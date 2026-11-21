<# .SYNOPSIS
Installs the pinned Inno Setup build compiler into an ignored per-user tool cache.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The Windows setup compiler requires Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $root 'installer/windows/toolchain.json') -Raw | ConvertFrom-Json
$cache = Join-Path $root ".validation/build-tools/inno-$($pin.version)"
$compiler = Join-Path $cache 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    Write-Output $compiler
    return
}
$download = Join-Path $root ".validation/build-tools/innosetup-$($pin.version).exe"
$fresh = -not (Test-Path -LiteralPath $download -PathType Leaf)
$installer = $download
if ($fresh) {
    $buildTools = Split-Path -Parent $download
    New-Item -ItemType Directory -Path $buildTools -Force | Out-Null
    $installer = Join-Path $buildTools ('.inno-download-' + [guid]::NewGuid().ToString('N') + '.exe')
}
try {
    if ($fresh) { Invoke-WebRequest -Uri $pin.url -OutFile $installer -TimeoutSec 180 }
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ine $pin.sha256) { throw 'Inno Setup download SHA-256 mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid') { throw 'Inno Setup compiler installer has no valid Authenticode signature.' }
    if ($fresh) { Move-Item -LiteralPath $installer -Destination $download }
} finally {
    if ($fresh -and (Test-Path -LiteralPath $installer)) { Remove-Item -LiteralPath $installer -Force }
}
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/NOICONS',('/DIR="{0}"' -f $cache),'/TASKS=""')
$process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw "Inno Setup compiler installation failed: $($process.ExitCode)." }
Write-Output $compiler
