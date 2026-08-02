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
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles '7-Zip\7z.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe') }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) {
        if ($command.Source) { return [string]$command.Source }
        if ($command.Path) { return [string]$command.Path }
    }
    return $null
}

function Invoke-Download {
    param([Parameter(Mandatory)][string]$Uri,[Parameter(Mandatory)][string]$OutFile)
    Write-BootstrapLog "Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $OutFile -Headers @{ 'User-Agent' = 'Parallax-Studio-Bootstrap' }
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -le 0) {
        throw "Download did not produce a valid file: $OutFile"
    }
}

function Add-LockEntry {
    param([Parameter(Mandatory)]$Entries,[Parameter(Mandatory)]$Dependency,[string]$SourceUrl,[string]$ArchivePath)
    $target = Join-Path $native $Dependency.outputFile
    $item = [ordered]@{
        id = $Dependency.id
        version = $Dependency.version
        architecture = $Dependency.architecture
        sourceUrl = $SourceUrl
        outputFile = $Dependency.outputFile
        outputSha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($ArchivePath -and (Test-Path -LiteralPath $ArchivePath)) {
        $item.archiveName = Split-Path -Leaf $ArchivePath
        $item.archiveSha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $Entries.Add($item)
}

New-Item -ItemType Directory -Force -Path $native, $cache | Out-Null
Set-Content -LiteralPath $bootstrapLog -Value ("Parallax Studio native bootstrap started: {0}" -f [DateTime]::Now.ToString('o')) -Encoding utf8

try {
    Write-Host 'Parallax Studio native dependency bootstrap started.' -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Native dependency manifest not found: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $lockEntries = [System.Collections.Generic.List[object]]::new()

    $mpv = $manifest.dependencies | Where-Object id -eq 'libmpv' | Select-Object -First 1
    $mpvTarget = Join-Path $native $mpv.outputFile
    if ($Force -or -not (Test-Path -LiteralPath $mpvTarget)) {
        $sevenZipPath = Resolve-SevenZip
        if (-not $sevenZipPath -and $InstallTools) {
            if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw '7-Zip is required and winget is unavailable.' }
            Write-Host 'Installing 7-Zip through winget...' -ForegroundColor Cyan
            & winget install --id 7zip.7zip --exact --silent --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -ne 0) { throw "winget failed to install 7-Zip (exit $LASTEXITCODE)." }
            $sevenZipPath = Resolve-SevenZip
        }
        if (-not $sevenZipPath) { throw "7-Zip is required. Install it or rerun with -InstallTools." }
        Write-Host "Using 7-Zip: $sevenZipPath" -ForegroundColor DarkCyan
        $archive = Join-Path $cache $mpv.archiveName
        if ($Force -or -not (Test-Path -LiteralPath $archive)) {
            Write-Host "Downloading pinned libmpv build $($mpv.version)..." -ForegroundColor Cyan
            Invoke-Download -Uri $mpv.downloadUrl -OutFile $archive
        } else { Write-Host "Using cached archive: $archive" -ForegroundColor DarkCyan }
        $extract = Join-Path $cache ('extract-mpv-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        try {
            Write-Host 'Extracting libmpv package...' -ForegroundColor Cyan
            & $sevenZipPath x $archive "-o$extract" -y
            if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with exit code $LASTEXITCODE." }
            $dll = Get-ChildItem -LiteralPath $extract -Filter 'libmpv-2.dll' -File -Recurse | Select-Object -First 1
            if (-not $dll) { throw 'The downloaded archive did not contain libmpv-2.dll.' }
            Copy-Item -LiteralPath $dll.FullName -Destination $mpvTarget -Force
        } finally { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host 'Installed libmpv-2.dll.' -ForegroundColor Green
    } else { Write-Host 'libmpv-2.dll already present; leaving it unchanged.' -ForegroundColor Green }
    Add-LockEntry -Entries $lockEntries -Dependency $mpv -SourceUrl $mpv.downloadUrl -ArchivePath (Join-Path $cache $mpv.archiveName)

    # libaacs and libbdplus come from the same attested Windows binary release.
    $aacs = $manifest.dependencies | Where-Object id -eq 'libaacs' | Select-Object -First 1
    $bdplus = $manifest.dependencies | Where-Object id -eq 'libbdplus' | Select-Object -First 1
    $needAacs = $Force -or -not (Test-Path -LiteralPath (Join-Path $native $aacs.outputFile))
    $needBdplus = $Force -or -not (Test-Path -LiteralPath (Join-Path $native $bdplus.outputFile))
    if ($needAacs -or $needBdplus) {
        try {
            Write-Host 'Resolving pinned libaacs/libbdplus Windows release...' -ForegroundColor Cyan
            $release = Invoke-RestMethod -Uri $aacs.releaseApiUrl -Headers @{ 'User-Agent' = 'Parallax-Studio-Bootstrap'; 'Accept' = 'application/vnd.github+json' }
            $asset = $release.assets | Where-Object { $_.name -match '\.(zip|7z)$' -and $_.name -notmatch 'installer|setup|source' } | Select-Object -First 1
            if (-not $asset) { throw 'No portable archive asset was found in the pinned libaacs/libbdplus release.' }
            $discArchive = Join-Path $cache $asset.name
            if ($Force -or -not (Test-Path -LiteralPath $discArchive)) {
                Write-Host "Downloading $($asset.name)..." -ForegroundColor Cyan
                Invoke-Download -Uri $asset.browser_download_url -OutFile $discArchive
            } else { Write-Host "Using cached archive: $discArchive" -ForegroundColor DarkCyan }
            $extract = Join-Path $cache ('extract-aacs-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path $extract | Out-Null
            try {
                Write-Host 'Extracting libaacs/libbdplus package...' -ForegroundColor Cyan
                if ([IO.Path]::GetExtension($discArchive) -ieq '.zip') {
                    Expand-Archive -LiteralPath $discArchive -DestinationPath $extract -Force
                } else {
                    $sevenZipPath = Resolve-SevenZip
                    if (-not $sevenZipPath) { throw '7-Zip is required to extract the libaacs archive.' }
                    & $sevenZipPath x $discArchive "-o$extract" -y
                    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with exit code $LASTEXITCODE." }
                }
                foreach ($dep in @($aacs,$bdplus)) {
                    $dll = Get-ChildItem -LiteralPath $extract -Filter $dep.outputFile -File -Recurse |
                        Where-Object { $_.FullName -match 'winx64|x64|amd64' } | Select-Object -First 1
                    if (-not $dll) { $dll = Get-ChildItem -LiteralPath $extract -Filter $dep.outputFile -File -Recurse | Select-Object -First 1 }
                    if (-not $dll) { throw "The archive did not contain $($dep.outputFile)." }
                    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $native $dep.outputFile) -Force
                    Write-Host "Installed $($dep.outputFile)." -ForegroundColor Green
                    Add-LockEntry -Entries $lockEntries -Dependency $dep -SourceUrl $asset.browser_download_url -ArchivePath $discArchive
                }
            } finally { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }
        } catch {
            Write-Warning "Optional Blu-ray libraries were not installed: $($_.Exception.Message)"
            Write-BootstrapLog "Optional libaacs/libbdplus failure: $($_ | Out-String)"
        }
    } else {
        Write-Host 'libaacs.dll and libbdplus.dll already present; leaving them unchanged.' -ForegroundColor Green
        foreach ($dep in @($aacs,$bdplus)) { Add-LockEntry -Entries $lockEntries -Dependency $dep -SourceUrl $dep.releaseApiUrl -ArchivePath $null }
    }

    # Current x64 libdvdcss, built from VideoLAN 1.5.0 source by the MSYS2 CI packager.
    # The previous 1.2.12 Windows binary predates multiple upstream crash fixes.
    $dvdcss = $manifest.dependencies | Where-Object id -eq 'libdvdcss' | Select-Object -First 1
    $dvdTarget = Join-Path $native $dvdcss.outputFile
    $lockedDvdVersion = $null
    if (Test-Path -LiteralPath $lockPath) {
        try {
            $previousLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
            $lockedDvdVersion = ($previousLock.dependencies | Where-Object id -eq 'libdvdcss' | Select-Object -First 1).version
        } catch {
            Write-BootstrapLog "Unable to read previous native lock for libdvdcss version check: $($_.Exception.Message)"
        }
    }
    $needDvdCss = $Force -or -not (Test-Path -LiteralPath $dvdTarget) -or $lockedDvdVersion -ne $dvdcss.version
    $dvdArchive = Join-Path $cache $dvdcss.packageName
    $dvdCssReady = -not $needDvdCss
    if ($needDvdCss) {
        try {
            $sevenZipPath = Resolve-SevenZip
            if (-not $sevenZipPath) { throw '7-Zip is required to extract the libdvdcss package.' }
            if ($Force -or -not (Test-Path -LiteralPath $dvdArchive)) {
                Write-Host "Downloading libdvdcss $($dvdcss.version) package..." -ForegroundColor Cyan
                Invoke-Download -Uri $dvdcss.downloadUrl -OutFile $dvdArchive
            } else {
                Write-Host "Using cached archive: $dvdArchive" -ForegroundColor DarkCyan
            }

            $packageHash = (Get-FileHash -LiteralPath $dvdArchive -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($packageHash -ne ([string]$dvdcss.packageSha256).ToLowerInvariant()) {
                Remove-Item -LiteralPath $dvdArchive -Force -ErrorAction SilentlyContinue
                throw "libdvdcss package checksum mismatch. Expected $($dvdcss.packageSha256), received $packageHash."
            }

            $extractOuter = Join-Path $cache ('extract-dvdcss-outer-' + [guid]::NewGuid().ToString('N'))
            $extractInner = Join-Path $cache ('extract-dvdcss-inner-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path $extractOuter, $extractInner | Out-Null
            try {
                & $sevenZipPath x $dvdArchive "-o$extractOuter" -y
                if ($LASTEXITCODE -ne 0) { throw "7-Zip failed to unpack the libdvdcss zstd layer (exit $LASTEXITCODE)." }
                $tarFile = Get-ChildItem -LiteralPath $extractOuter -Filter '*.tar' -File -Recurse | Select-Object -First 1
                if (-not $tarFile) { throw 'The libdvdcss package did not contain a tar archive.' }
                & $sevenZipPath x $tarFile.FullName "-o$extractInner" -y
                if ($LASTEXITCODE -ne 0) { throw "7-Zip failed to unpack the libdvdcss tar layer (exit $LASTEXITCODE)." }
                $dvdDll = Get-ChildItem -LiteralPath $extractInner -Filter $dvdcss.outputFile -File -Recurse | Select-Object -First 1
                if (-not $dvdDll) { throw "The libdvdcss package did not contain $($dvdcss.outputFile)." }
                Copy-Item -LiteralPath $dvdDll.FullName -Destination $dvdTarget -Force
            } finally {
                Remove-Item -LiteralPath $extractOuter, $extractInner -Recurse -Force -ErrorAction SilentlyContinue
            }
            $dvdCssReady = $true
            Write-Host "Installed libdvdcss $($dvdcss.version)." -ForegroundColor Green
        } catch {
            $dvdCssReady = $false
            Remove-Item -LiteralPath $dvdTarget -Force -ErrorAction SilentlyContinue
            Write-Warning "Optional DVD library was not installed: $($_.Exception.Message)"
            Write-BootstrapLog "Optional libdvdcss failure: $($_ | Out-String)"
        }
    } else {
        Write-Host "libdvdcss $($dvdcss.version) already present; leaving it unchanged." -ForegroundColor Green
    }
    if ($dvdCssReady -and (Test-Path -LiteralPath $dvdTarget)) {
        Add-LockEntry -Entries $lockEntries -Dependency $dvdcss -SourceUrl $dvdcss.downloadUrl -ArchivePath $dvdArchive
    }

    [ordered]@{
        schemaVersion = 2
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        dependencies = $lockEntries
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $lockPath -Encoding utf8
    Write-Host 'Wrote native-dependencies.lock.json.' -ForegroundColor Green

    & (Join-Path $PSScriptRoot 'Verify-NativeDependencies.ps1')
    Write-BootstrapLog 'Bootstrap completed successfully.'
    Write-Host 'Parallax Studio native dependency bootstrap finished.' -ForegroundColor Green
}
catch {
    $details = ($_ | Out-String).Trim()
    try { Write-BootstrapLog ("FAILED: $details") } catch { }
    throw
}
