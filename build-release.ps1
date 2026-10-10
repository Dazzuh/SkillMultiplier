# PowerShell script to build and package SkillMultiplier for release
param(
    [Parameter(Mandatory, HelpMessage = 'Path to the SPT install to build against (the folder holding EscapeFromTarkov.exe)')]
    [string]$SPTDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Variables
$projectDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Path)"
$srcDir = Join-Path $projectDir "src"
$serverDir = Join-Path $projectDir "SkillMultiplier-server"
# Build-output locations follow the configuration being built ($Configuration) and each project's
# declared layout. Verified against real builds (Debug and Release both land the server DLL in
# bin\<Configuration>\SkillMultiplier-server\): a path that names the wrong configuration or a
# deeper directory would package older DLLs - or nothing - while the hash check compared the
# archive against itself and passed.
$buildDir = Join-Path $srcDir "bin\$Configuration\netstandard2.1"
$serverBuildDir = Join-Path $serverDir "bin\$Configuration\SkillMultiplier-server"
$releaseDir = Join-Path $projectDir "release"
$pluginName = "dazzuh.skillmultiplier.dll"
$serverPluginName = "SkillMultiplier-server.dll"
$pluginSource = Join-Path $buildDir $pluginName
$serverSource = Join-Path $serverBuildDir $serverPluginName
$version = "unknown"

# Refuse a target that is not an SPT install, same as deploy.ps1: the build needs
# its assemblies, and building against the wrong one fails stranger later.
$runtime = Join-Path $SPTDir 'SPT_Runtime'
if (-not (Test-Path (Join-Path $SPTDir 'EscapeFromTarkov.exe')) -or -not (Test-Path $runtime)) {
    throw "Does not look like an SPT install: $SPTDir (expected EscapeFromTarkov.exe and SPT_Runtime inside it)"
}

# Build first. This script used to zip whatever bin/ held, which silently packaged
# yesterday's DLLs after a source-only change - build what you ship.
$serverProj = Join-Path $serverDir 'SkillMultiplier-server.csproj'
Write-Host "Building $serverProj ($Configuration)"
dotnet build "$serverProj" -c "$Configuration" -p:SPTRuntimeDir="$runtime"
if ($LASTEXITCODE -ne 0) { throw 'server build failed' }

$clientProj = Join-Path $srcDir 'SkillMultiplier.csproj'
Write-Host "Building $clientProj ($Configuration)"
dotnet build "$clientProj" -c "$Configuration" -p:SPTDir="$SPTDir"
if ($LASTEXITCODE -ne 0) { throw 'client build failed' }

if (-not (Test-Path $pluginSource)) { throw "no built client at $pluginSource" }
if (-not (Test-Path $serverSource)) { throw "no built server at $serverSource" }

# Single source of truth: Directory.Build.props at the repo root. Both halves inherit it, and the
# server's ModMetadata reads it back out of the built assembly, so a skew is impossible by
# construction - which makes the local re-declaration the only way it can come back.
$propsPath = Join-Path $projectDir "Directory.Build.props"
if (-not (Test-Path $propsPath)) { throw "no version source at $propsPath" }
[xml]$propsXml = Get-Content $propsPath
$version = $propsXml.Project.PropertyGroup.Version
if (-not $version) { throw "Directory.Build.props declares no Version" }
$version = $version.Trim()

# Guard the consolidation: a <Version> inside either half would silently shadow the shared one,
# and the two halves would then be free to drift apart again.
foreach ($csproj in @((Join-Path $srcDir "SkillMultiplier.csproj"), (Join-Path $serverDir "SkillMultiplier-server.csproj"))) {
    [xml]$halfXml = Get-Content $csproj
    if ($halfXml.Project.PropertyGroup | Where-Object { $_.Version }) {
        throw "$csproj declares its own Version - remove it and let Directory.Build.props own it"
    }
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

# Copy server files. wwwroot is the UI, and SPT serves it out of the mod folder - a release without it is a
# mod with no page.
$serverModDir = Join-Path $tempDir "SPT_Runtime/user/mods/dazzuh-skillmultiplier"
Copy-Item (Join-Path $serverBuildDir $serverPluginName) $serverModDir -Force
# config.json is deliberately NOT shipped: it is the user's file, and a release extracted over an existing
# install would overwrite their tuning with defaults. A fresh install gets one seeded by the server itself
# on first boot (LoadConfig writes the default when the file is absent).
Copy-Item (Join-Path $serverBuildDir "wwwroot") $serverModDir -Recurse -Force
Write-Host "Copied server files to: SPT_Runtime/user/mods/dazzuh-skillmultiplier/"

# Create zip
Compress-Archive -Path (Join-Path $tempDir "BepInEx"), (Join-Path $tempDir "SPT_Runtime") -DestinationPath $zipPath

# Prove the zip holds what was just built: read both DLLs back out and compare hashes.
# "Packaged" is a claim about the bytes in the archive, not about the copy succeeding.
# The page ships in the same zip and has no compiler watching it, so its entry file is checked too -
# a stale app.js otherwise ships green.
$checkDir = Join-Path $releaseDir "temp-check"
if (Test-Path $checkDir) {
    Remove-Item $checkDir -Recurse -Force
}
New-Item -ItemType Directory -Path $checkDir -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $checkDir -Force
$zippedClient = Join-Path $checkDir "BepInEx/plugins/$pluginName"
$zippedServer = Join-Path $checkDir "SPT_Runtime/user/mods/dazzuh-skillmultiplier/$serverPluginName"
$zippedJs = Join-Path $checkDir "SPT_Runtime/user/mods/dazzuh-skillmultiplier/wwwroot/app.js"
$clientHash = (Get-FileHash $pluginSource).Hash
$serverHash = (Get-FileHash $serverSource).Hash
if ((Get-FileHash $zippedClient).Hash -ne $clientHash) {
    throw "the zip does not contain the client just built: $zipPath"
}
if ((Get-FileHash $zippedServer).Hash -ne $serverHash) {
    throw "the zip does not contain the server just built: $zipPath"
}
if ((Get-FileHash (Join-Path $serverBuildDir "wwwroot/app.js")).Hash -ne (Get-FileHash $zippedJs).Hash) {
    throw "the zip does not contain the page just built: $zipPath"
}
Write-Host "Verified zip contents: client $clientHash / server $serverHash"
Remove-Item $checkDir -Recurse -Force

# Clean up temp
Remove-Item $tempDir -Recurse -Force

Write-Host "Release zip created at: $zipPath"
