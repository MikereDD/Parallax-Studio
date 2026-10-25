[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$PreviewDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'tests\ParallaxStudio.Presentation.Tests\ParallaxStudio.Presentation.Tests.csproj'

& dotnet build $projectPath --configuration $Configuration --verbosity minimal -m:1 -p:BuildInParallel=false
if ($LASTEXITCODE -ne 0) {
    throw "Presentation test build failed with exit code $LASTEXITCODE."
}

$runArguments = @('run', '--project', $projectPath, '--configuration', $Configuration, '--no-build', '--no-restore')
if ($PreviewDirectory) {
    $runArguments += @('--', '--preview', [System.IO.Path]::GetFullPath($PreviewDirectory))
}
& dotnet @runArguments
if ($LASTEXITCODE -ne 0) {
    throw "Presentation checks failed with exit code $LASTEXITCODE."
}
