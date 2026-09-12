# PowerShell script to build and package SkillMultiplier for release

# Variables
$projectDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Path)"
$srcDir = Join-Path $projectDir "src"
$serverDir = Join-Path $projectDir "SkillMultiplier-server"
$buildDir = Join-Path $srcDir "bin\Release\netstandard2.1"
$serverBuildDir = Join-Path $serverDir "bin\Release\SkillMultiplier-server"
$releaseDir = Join-Path $projectDir "release"
$pluginName = "dazzuh.skillmultiplier.dll"
$serverPluginName = "SkillMultiplier-server.dll"
$pluginSource = Join-Path $buildDir $pluginName
$serverSource = Join-Path $serverBuildDir $serverPluginName
$version = "unknown"

# Get version from csproj
$csprojPath = Join-Path $srcDir "SkillMultiplier.csproj"
[xml]$csprojXml = Get-Content $csprojPath
$version = $csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -ExpandProperty Version -First 1
if ($version) {
    $version = $version.Trim()
} else {
    $version = "unknown"
}

$zipName = "SkillMultiplier-$version.zip"
$zipPath = Join-Path $releaseDir $zipName
$targetPluginPath = "BepInEx/plugins/$pluginName"

# Ensure release directory exists
if (!(Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

# Clean previous release zip
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}

# Prepare temp structure
$tempDir = Join-Path $releaseDir "temp"
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $tempDir "BepInEx/plugins") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $tempDir "SPT_Runtime/user/mods/dazzuh-skillmultiplier") -Force | Out-Null

# Copy plugin
Copy-Item $pluginSource (Join-Path $tempDir $targetPluginPath) -Force
Write-Host "Copied plugin: $pluginName"

# Copy server files
$serverModDir = Join-Path $tempDir "SPT_Runtime/user/mods/dazzuh-skillmultiplier"
Copy-Item (Join-Path $serverBuildDir $serverPluginName) $serverModDir -Recurse -Force
Copy-Item (Join-Path $serverBuildDir "config.json") $serverModDir -Recurse -Force
Write-Host "Copied server files to: SPT_Runtime/user/mods/dazzuh-skillmultiplier/"

# Create zip
Compress-Archive -Path (Join-Path $tempDir "BepInEx"), (Join-Path $tempDir "SPT_Runtime") -DestinationPath $zipPath

# Clean up temp
Remove-Item $tempDir -Recurse -Force

Write-Host "Release zip created at: $zipPath"
