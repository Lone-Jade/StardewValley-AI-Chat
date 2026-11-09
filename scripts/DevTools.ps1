function Initialize-DevEnvironment {
    param([Parameter(Mandatory)][string]$Root)

    $env:DOTNET_CLI_HOME = Join-Path $Root '.tools/cli-home'
    $env:NUGET_PACKAGES = Join-Path $Root '.tools/nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $Root '.tools/nuget-http-cache'
    $env:NUGET_SCRATCH = Join-Path $Root '.tools/nuget-scratch'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $localSdk = Join-Path $Root '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) {
        return $localSdk
    }
    $installedSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$installedSdk) {
        throw 'Install the .NET SDK specified in global.json. See README.md.'
    }
    return $installedSdk.Source
}

function Resolve-StardewGamePath {
    param([string]$Root, [string]$Dotnet, [string]$GamePath)

    if ([string]::IsNullOrWhiteSpace($GamePath)) {
        $project = Join-Path $Root 'src/StardewValley-AI-Chat/StardewValley-AI-Chat.csproj'
        $detected = & $Dotnet msbuild $project -nologo -getProperty:GamePath
        if ($LASTEXITCODE -ne 0) {
            throw 'Game path detection failed. Pass -GamePath or set STARDEW_GAME_PATH.'
        }
        $GamePath = ($detected -join [Environment]::NewLine).Trim()
    }
    if ([string]::IsNullOrWhiteSpace($GamePath) -or !(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.exe'))) {
        throw 'SMAPI was not found. Pass -GamePath or set STARDEW_GAME_PATH.'
    }
    return (Resolve-Path -LiteralPath $GamePath).Path
}
