[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Clean,
    [switch]$Run,
    [switch]$Bootstrap,
    [switch]$InstallBootstrapTools
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'ParallaxStudio.sln'
$appProject = Join-Path $root 'src\ParallaxStudio.App\ParallaxStudio.App.csproj'

Write-Host 'Parallax Studio build script started.' -ForegroundColor DarkCyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK and try again.'
}

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^(\d+)\.') {
    throw 'Unable to determine the installed .NET SDK version.'
}

if ([int]$Matches[1] -lt 10) {
    throw "Parallax Studio requires the .NET 10 SDK. Found: $dotnetVersion"
}

if ($Bootstrap) {
    $bootstrapScript = Join-Path $root 'scripts\Get-NativeDependencies.ps1'
    Write-Host 'Bootstrapping native dependencies...' -ForegroundColor Cyan

    if ($InstallBootstrapTools) {
        & $bootstrapScript -InstallTools
    }
    else {
        & $bootstrapScript
    }

    if (-not $?) {
        throw 'Native dependency bootstrap did not complete successfully.'
    }

    Write-Host 'Native dependency bootstrap completed.' -ForegroundColor Green
}

Write-Host "Building Parallax Studio v1.0-dev.5.2.1 ($Configuration)..." -ForegroundColor Cyan

if ($Clean) {
    & dotnet clean $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE." }
}

& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

& dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

# Stage MakeMKV LibMMBD under the DLL names libbluray/libmpv requests on Windows.
# The source native directory remains untouched; only this build output is changed.
$appOutput = Join-Path $root "src\ParallaxStudio.App\bin\$Configuration\net10.0-windows\win-x64"
$makeMkvCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'MakeMKV\libmmbd64.dll'),
    (Join-Path $env:ProgramFiles 'MakeMKV\libmmbd64.dll')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$makeMkvLibMmbd = $makeMkvCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if ($makeMkvLibMmbd) {
    $standardAacs = Join-Path $root 'native\libaacs.dll'
    $standardBdPlus = Join-Path $root 'native\libbdplus.dll'
    $runtimeAacs = Join-Path $appOutput 'libaacs.dll'
    $runtimeBdPlus = Join-Path $appOutput 'libbdplus.dll'

    New-Item -ItemType Directory -Path $appOutput -Force | Out-Null

    if (Test-Path -LiteralPath $standardAacs) {
        Copy-Item -LiteralPath $standardAacs -Destination (Join-Path $appOutput 'libaacs.standard.dll') -Force
    }
    if (Test-Path -LiteralPath $standardBdPlus) {
        Copy-Item -LiteralPath $standardBdPlus -Destination (Join-Path $appOutput 'libbdplus.standard.dll') -Force
    }

    Copy-Item -LiteralPath $makeMkvLibMmbd -Destination (Join-Path $appOutput 'libmmbd64.dll') -Force
    Copy-Item -LiteralPath $makeMkvLibMmbd -Destination $runtimeAacs -Force
    Copy-Item -LiteralPath $makeMkvLibMmbd -Destination $runtimeBdPlus -Force

    $providerHash = (Get-FileHash -LiteralPath $makeMkvLibMmbd -Algorithm SHA256).Hash.ToLowerInvariant()
    $aacsHash = (Get-FileHash -LiteralPath $runtimeAacs -Algorithm SHA256).Hash.ToLowerInvariant()
    $bdPlusHash = (Get-FileHash -LiteralPath $runtimeBdPlus -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($providerHash -ne $aacsHash -or $providerHash -ne $bdPlusHash) {
        throw 'MakeMKV LibMMBD staging verification failed.'
    }

    Write-Host "MakeMKV LibMMBD staged for protected Blu-ray playback:" -ForegroundColor Green
    Write-Host "  provider: $makeMkvLibMmbd"
    Write-Host "  libaacs.dll: $runtimeAacs"
    Write-Host "  libbdplus.dll: $runtimeBdPlus"
    Write-Host "  SHA-256: $providerHash"
}
else {
    Write-Warning 'MakeMKV libmmbd64.dll was not found. Standard libaacs/libbdplus will remain active.'
}

$nativeDll = Join-Path $root 'native\libmpv-2.dll'
if (-not (Test-Path -LiteralPath $nativeDll)) {
    Write-Warning "libmpv-2.dll is missing. Run '.\build.ps1 -Bootstrap' to download it."
}

if ($Run) {
    Write-Host 'Launching Parallax Studio...' -ForegroundColor Cyan
    & dotnet run --project $appProject -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Parallax Studio exited with code $LASTEXITCODE." }
}
else {
    Write-Host 'Build complete.' -ForegroundColor Green
}
