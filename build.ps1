param(
    [string]$GamePath = $env:STARDEW_GAME_PATH
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/DevTools.ps1')
$dotnet = Initialize-DevEnvironment -Root $PSScriptRoot

Push-Location $PSScriptRoot
try {
    $buildArguments = @('build', 'src/StardewValley-AI-Chat/StardewValley-AI-Chat.csproj', '--configuration', 'Debug')
    if (![string]::IsNullOrWhiteSpace($GamePath)) {
        $buildArguments += "-p:GamePath=$GamePath"
    }
    & $dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
