[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$native = Join-Path $root 'native'
$manifest = Get-Content -LiteralPath (Join-Path $root 'native-dependencies.json') -Raw | ConvertFrom-Json
$lockPath = Join-Path $root 'native-dependencies.lock.json'
$failed = $false

foreach ($dependency in $manifest.dependencies) {
    $path = Join-Path $native $dependency.outputFile
    if (-not (Test-Path -LiteralPath $path)) {
        $label = if ($dependency.required) { 'MISSING (required)' } else { 'not installed (optional)' }
        Write-Host "$($dependency.id): $label" -ForegroundColor $(if ($dependency.required) { 'Red' } else { 'DarkYellow' })
        if ($dependency.required) { $failed = $true }
        continue
    }
    Write-Host "$($dependency.id): present" -ForegroundColor Green
}

if (Test-Path -LiteralPath $lockPath) {
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    foreach ($entry in $lock.dependencies) {
        $path = Join-Path $native $entry.outputFile
        if (Test-Path -LiteralPath $path) {
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $entry.outputSha256) {
                Write-Host "$($entry.id): checksum mismatch" -ForegroundColor Red
                $failed = $true
            } else {
                Write-Host "$($entry.id): checksum verified" -ForegroundColor Green
            }
        }
    }
} else {
    Write-Host 'No lock file yet. Run Get-NativeDependencies.ps1 once to create it.' -ForegroundColor DarkYellow
}

if ($failed) { throw 'Native dependency verification failed.' }
Write-Host 'Native dependency verification passed.' -ForegroundColor Green
