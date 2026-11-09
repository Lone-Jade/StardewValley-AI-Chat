$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/DevTools.ps1')
$dotnet = Initialize-DevEnvironment -Root $PSScriptRoot
$env:STARDEWAI_TEST_TEMP = Join-Path $PSScriptRoot 'artifacts\offline-test-temp'

Push-Location $PSScriptRoot
try {
    & $dotnet run --project 'tests\StardewValley-AI-Chat.OfflineTests\StardewValley-AI-Chat.OfflineTests.csproj'
    if ($LASTEXITCODE -ne 0) {
        throw "Offline tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
