[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Clean,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'ParallaxStudio.sln'
$appProject = Join-Path $root 'src\ParallaxStudio.App\ParallaxStudio.App.csproj'

function Get-DotNetMajorVersion {
    $raw = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $raw -notmatch '^(\d+)\.') {
        throw "Unable to determine the installed .NET SDK version."
    }
    return [int]$Matches[1]
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK and try again.'
}

$major = Get-DotNetMajorVersion
if ($major -lt 10) {
    throw "Parallax Studio requires the .NET 10 SDK. Found: $(& dotnet --version)"
}

Write-Host "Building Parallax Studio v1.0-dev.4.1 ($Configuration)..." -ForegroundColor Cyan

if ($Clean) {
    & dotnet clean $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$nativeDll = Join-Path $root 'native\libmpv-2.dll'
if (-not (Test-Path -LiteralPath $nativeDll)) {
    Write-Warning 'libmpv-2.dll is not in .\native yet. The shell will run, but playback will remain disconnected.'
}

if ($Run) {
    & dotnet run --project $appProject -c $Configuration --no-build
    exit $LASTEXITCODE
}

Write-Host 'Build complete.' -ForegroundColor Green
