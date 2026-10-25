[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\ParallaxStudio.TmdbArtwork.Tests\ParallaxStudio.TmdbArtwork.Tests.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "TMDB artwork test project not found: $project"
}

dotnet run --project $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "TMDB artwork tests failed with exit code $LASTEXITCODE."
}
