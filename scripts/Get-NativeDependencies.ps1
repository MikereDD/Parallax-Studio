[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$InstallTools
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$native = Join-Path $root 'native'
$cache = Join-Path $native '.downloads'
$manifestPath = Join-Path $root 'native-dependencies.json'
$lockPath = Join-Path $root 'native-dependencies.lock.json'
$bootstrapLog = Join-Path $native 'bootstrap.log'

function Write-BootstrapLog {
    param([Parameter(Mandatory)][string]$Message)
    $line = '{0}  {1}' -f ([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')), $Message
    Add-Content -LiteralPath $bootstrapLog -Value $line -Encoding utf8
}

function Resolve-SevenZip {
    $candidates = @()

    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles '7-Zip\7z.exe')
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
    }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe')
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) {
        if ($command.Source) { return [string]$command.Source }
        if ($command.Path) { return [string]$command.Path }
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $native, $cache | Out-Null
Set-Content -LiteralPath $bootstrapLog -Value ("Parallax Studio native bootstrap started: {0}" -f [DateTime]::Now.ToString('o')) -Encoding utf8

try {
    Write-Host 'Parallax Studio native dependency bootstrap started.' -ForegroundColor Cyan
    Write-BootstrapLog 'Entered bootstrap script.'

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Native dependency manifest not found: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $mpv = $manifest.dependencies | Where-Object { $_.id -eq 'libmpv' } | Select-Object -First 1
    if (-not $mpv) { throw 'libmpv is missing from native-dependencies.json.' }

    $target = Join-Path $native $mpv.outputFile
    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        Write-Host "libmpv already exists: $target" -ForegroundColor Green
        Write-BootstrapLog 'Existing libmpv found; verifying.'
        & (Join-Path $PSScriptRoot 'Verify-NativeDependencies.ps1')
        Write-BootstrapLog 'Existing libmpv verified.'
        Write-Host 'Parallax Studio native dependency bootstrap finished.' -ForegroundColor Green
        return
    }

    $sevenZipPath = Resolve-SevenZip
    if (-not $sevenZipPath -and $InstallTools) {
        if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
            throw '7-Zip is required and winget is unavailable. Install 7-Zip, then run this script again.'
        }

        Write-Host 'Installing 7-Zip through winget...' -ForegroundColor Cyan
        Write-BootstrapLog 'Invoking winget for 7-Zip.'
        & winget install --id 7zip.7zip --exact --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget failed to install 7-Zip (exit $LASTEXITCODE)."
        }

        $sevenZipPath = Resolve-SevenZip
    }

    if (-not $sevenZipPath) {
        throw "7-Zip is required. Install it with 'winget install 7zip.7zip' or rerun with -InstallTools."
    }

    Write-Host "Using 7-Zip: $sevenZipPath" -ForegroundColor DarkCyan
    Write-BootstrapLog "Using 7-Zip: $sevenZipPath"

    $archive = Join-Path $cache $mpv.archiveName
    if ($Force -or -not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading pinned libmpv build $($mpv.version)..." -ForegroundColor Cyan
        Write-BootstrapLog "Downloading: $($mpv.downloadUrl)"
        Invoke-WebRequest -Uri $mpv.downloadUrl -OutFile $archive
    }
    else {
        Write-Host "Using cached archive: $archive" -ForegroundColor DarkCyan
        Write-BootstrapLog "Using cached archive: $archive"
    }

    if (-not (Test-Path -LiteralPath $archive)) {
        throw "The libmpv archive was not found after download: $archive"
    }

    $archiveInfo = Get-Item -LiteralPath $archive
    if ($archiveInfo.Length -le 0) {
        throw "The downloaded libmpv archive is empty: $archive"
    }

    $extract = Join-Path $cache ('extract-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $extract | Out-Null

    try {
        Write-Host 'Extracting libmpv package...' -ForegroundColor Cyan
        Write-BootstrapLog "Extracting archive to: $extract"

        & $sevenZipPath x $archive "-o$extract" -y
        $sevenZipExitCode = $LASTEXITCODE
        if ($sevenZipExitCode -ne 0) {
            throw "7-Zip extraction failed with exit code $sevenZipExitCode."
        }

        $dll = Get-ChildItem -LiteralPath $extract -Filter 'libmpv-2.dll' -File -Recurse | Select-Object -First 1
        if (-not $dll) { throw 'The downloaded archive did not contain libmpv-2.dll.' }

        Copy-Item -LiteralPath $dll.FullName -Destination $target -Force

        if (-not (Test-Path -LiteralPath $target)) {
            throw "libmpv-2.dll was not installed at the expected path: $target"
        }

        $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $dllHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()

        [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString('o')
            dependencies = @(
                [ordered]@{
                    id = 'libmpv'
                    version = $mpv.version
                    architecture = $mpv.architecture
                    sourceUrl = $mpv.downloadUrl
                    archiveName = $mpv.archiveName
                    archiveSha256 = $archiveHash
                    outputFile = $mpv.outputFile
                    outputSha256 = $dllHash
                }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $lockPath -Encoding utf8

        Write-Host "Installed $($mpv.outputFile)." -ForegroundColor Green
        Write-Host 'Wrote native-dependencies.lock.json.' -ForegroundColor Green
        Write-BootstrapLog "Installed $($mpv.outputFile); SHA-256 $dllHash"
    }
    finally {
        Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
    }

    & (Join-Path $PSScriptRoot 'Verify-NativeDependencies.ps1')
    Write-BootstrapLog 'Bootstrap completed successfully.'
    Write-Host 'Parallax Studio native dependency bootstrap finished.' -ForegroundColor Green
}
catch {
    $details = ($_ | Out-String).Trim()
    try { Write-BootstrapLog ("FAILED: $details") } catch { }
    throw
}
