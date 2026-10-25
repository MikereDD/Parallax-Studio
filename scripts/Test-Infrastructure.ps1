[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    # Optional substring filter for check names, for example 'service:' or 'hung'.
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\ParallaxStudio.Infrastructure.Tests\ParallaxStudio.Infrastructure.Tests.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Infrastructure test project not found: $project"
}

$runArguments = @('run', '--project', $project, '-c', $Configuration)
if ($Filter) { $runArguments += @('--', $Filter) }

& dotnet @runArguments
if ($LASTEXITCODE -ne 0) {
    throw "Infrastructure tests failed with exit code $LASTEXITCODE."
}
