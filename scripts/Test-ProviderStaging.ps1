[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Verifies that the optical-disc decryption provider staged in the app output is consistent
# with the supported build contract:
#   - libmmbd.staged present  => libaacs.dll/libbdplus.dll must be the staged MakeMKV LibMMBD.
#   - libmmbd.staged absent   => libaacs.dll/libbdplus.dll must be the standard native libraries.
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appOutput = Join-Path $root "src\ParallaxStudio.App\bin\$Configuration\net10.0-windows\win-x64"
$markerPath = Join-Path $appOutput 'libmmbd.staged'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $appOutput)) {
    throw "App output not found: $appOutput. Build the $Configuration configuration first."
}

if (Test-Path -LiteralPath $markerPath) {
    $marker = @{}
    foreach ($line in (Get-Content -LiteralPath $markerPath)) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) { $marker[$parts[0]] = $parts[1] }
    }
    $expectedHash = $marker['sha256']
    if ([string]::IsNullOrWhiteSpace($expectedHash)) {
        throw "LibMMBD marker is missing its sha256 entry: $markerPath"
    }

    foreach ($alias in 'libaacs.dll', 'libbdplus.dll', 'libmmbd64.dll') {
        $aliasPath = Join-Path $appOutput $alias
        if (-not (Test-Path -LiteralPath $aliasPath)) {
            throw "LibMMBD marker is present but $alias is missing from $appOutput."
        }
        $actualHash = Get-Sha256 $aliasPath
        if ($actualHash -ne $expectedHash) {
            throw "Provider mismatch: $alias in $appOutput does not match the staged LibMMBD SHA-256 ($expectedHash)."
        }
    }

    Write-Host "Provider verification passed ($Configuration): MakeMKV LibMMBD staged." -ForegroundColor Green
    Write-Host "  provider: $($marker['provider'])"
    Write-Host "  SHA-256: $expectedHash"
    return
}

$failed = $false
foreach ($pair in @(@('native\libaacs.dll', 'libaacs.dll'), @('native\libbdplus.dll', 'libbdplus.dll'))) {
    $standardPath = Join-Path $root $pair[0]
    $runtimePath = Join-Path $appOutput $pair[1]
    if (-not (Test-Path -LiteralPath $standardPath)) {
        Write-Host "$($pair[1]): standard library not bootstrapped; skipped." -ForegroundColor DarkYellow
        continue
    }
    if (-not (Test-Path -LiteralPath $runtimePath)) {
        Write-Host "$($pair[1]): missing from $appOutput" -ForegroundColor Red
        $failed = $true
        continue
    }
    if ((Get-Sha256 $runtimePath) -ne (Get-Sha256 $standardPath)) {
        Write-Host "$($pair[1]): output does not match the standard library and no LibMMBD marker is present" -ForegroundColor Red
        $failed = $true
        continue
    }
    Write-Host "$($pair[1]): standard provider verified" -ForegroundColor Green
}

if ($failed) { throw 'Provider staging verification failed.' }
Write-Host "Provider verification passed ($Configuration): standard libaacs/libbdplus." -ForegroundColor Green
