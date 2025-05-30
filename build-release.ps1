# PowerShell script to build and package SkillMultiplier for release

# Variables
$projectDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Path)"
$srcDir = Join-Path $projectDir "src"
$serverDir = Join-Path $projectDir "server"
$buildDir = Join-Path $srcDir "bin\Debug\netstandard2.1"
$releaseDir = Join-Path $projectDir "release"
$pluginName = "dazzuh.skillmultiplier.dll"
$pluginSource = Join-Path $buildDir $pluginName
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

# Update server package.json version
$packageJsonPath = Join-Path $serverDir "package.json"
if (Test-Path $packageJsonPath) {
    $packageJson = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
    $packageJson.version = $version
    $packageJson | ConvertTo-Json -Depth 10 | Set-Content $packageJsonPath -Encoding UTF8
    Write-Host "Updated server package.json version to: $version"
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
New-Item -ItemType Directory -Path (Join-Path $tempDir "user/mods/dazzuh-skillmultiplier") -Force | Out-Null

# Copy plugin
Copy-Item $pluginSource (Join-Path $tempDir $targetPluginPath) -Force
Write-Host "Copied plugin: $pluginName"

# Copy server files
$serverModDir = Join-Path $tempDir "user/mods/dazzuh-skillmultiplier"
Copy-Item (Join-Path $serverDir "package.json") $serverModDir -Force
Copy-Item (Join-Path $serverDir "src") $serverModDir -Recurse -Force
Write-Host "Copied server files to: user/mods/dazzuh-skillmultiplier/"

# Create zip
Compress-Archive -Path (Join-Path $tempDir "BepInEx"), (Join-Path $tempDir "user") -DestinationPath $zipPath

# Clean up temp
Remove-Item $tempDir -Recurse -Force

Write-Host "Release zip created at: $zipPath"
