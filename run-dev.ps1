param(
    [string]$GamePath = $env:STARDEW_GAME_PATH
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/DevTools.ps1')
$dotnet = Initialize-DevEnvironment -Root $PSScriptRoot
$modsPath = Join-Path $PSScriptRoot 'artifacts\Mods'
$entryDll = Join-Path $modsPath 'StardewValley-AI-Chat\StardewValley-AI-Chat.dll'
if (!(Test-Path -LiteralPath $entryDll)) {
    throw 'Build first with .\build.ps1.'
}
if (Get-Process -Name StardewModdingAPI,'Stardew Valley' -ErrorAction SilentlyContinue) {
    throw 'A game instance is already running. Close it before starting the development instance.'
}
$GamePath = Resolve-StardewGamePath -Root $PSScriptRoot -Dotnet $dotnet -GamePath $GamePath

Push-Location $GamePath
try {
    & (Join-Path $GamePath 'StardewModdingAPI.exe') --mods-path $modsPath
}
finally {
    Pop-Location
}
