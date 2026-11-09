param(
    [string]$GamePath = $env:STARDEW_GAME_PATH
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -GamePath $GamePath

$projectPath = Join-Path $PSScriptRoot 'src/StardewValley-AI-Chat'
$manifest = Get-Content -LiteralPath (Join-Path $projectPath 'manifest.json') -Raw | ConvertFrom-Json
$modName = 'StardewValley-AI-Chat'
$version = $manifest.Version
$buildZip = Join-Path $projectPath "bin/Debug/net6.0/$modName $version.zip"
$outputDirectory = Join-Path $PSScriptRoot 'artifacts/nexus'
$stagingDirectory = Join-Path $outputDirectory ('staging/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($buildZip, $stagingDirectory)
$modDirectory = Join-Path $stagingDirectory $modName

# Public config is always created from the checked-in, empty-key example.
$examplePath = Join-Path $modDirectory 'config.example.json'
$config = Get-Content -LiteralPath $examplePath -Raw | ConvertFrom-Json
if ($config.AI.ApiKey -ne '' -or $config.AI.Enabled -or $config.AI.Provider -ne 'deepseek') {
    throw 'The public configuration must use DeepSeek with an empty key and AI disabled.'
}
Copy-Item -LiteralPath $examplePath -Destination (Join-Path $modDirectory 'config.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/NEXUS_README.md') -Destination (Join-Path $modDirectory 'README.md') -Force

$required = @('manifest.json', $manifest.EntryDll, 'config.json', 'README.md', 'LICENSE', 'i18n/default.json', 'assets/icon.png')
foreach ($relativePath in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $modDirectory $relativePath) -PathType Leaf)) {
        throw "Missing package file: $relativePath"
    }
}
$translations = Get-Content -LiteralPath (Join-Path $modDirectory 'i18n/default.json') -Raw | ConvertFrom-Json
if ($translations.PSObject.Properties.Count -eq 0) {
    throw 'Default translations are empty.'
}
foreach ($file in Get-ChildItem -LiteralPath $modDirectory -Recurse -File) {
    $relativePath = [IO.Path]::GetRelativePath($modDirectory, $file.FullName).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    if ($relativePath -match '(^|/)(\.git|bin|obj|DialogueHistory|ConversationMemory)(/|$)|\.(log|bak|bundle|user)$') {
        throw "Private or development file in package: $relativePath"
    }
    if ($file.Extension -eq '.json') {
        $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($json.AI -and ($json.AI.ApiKey -ne '' -or $json.AI.Enabled)) {
            throw "Credentials or enabled AI in package: $relativePath"
        }
    }
}

$destination = Join-Path $outputDirectory "$modName-$version-Nexus.zip"
$temporaryZip = Join-Path $outputDirectory ([Guid]::NewGuid().ToString('N') + '.zip.tmp')
[IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath $temporaryZip -Destination $destination -Force
Write-Output $destination
