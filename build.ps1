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

Write-Host "Building Parallax Studio v1.0-dev.4.3 ($Configuration)..." -ForegroundColor Cyan

if ($Clean) {
    & dotnet clean $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE." }
}

& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

& dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

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
