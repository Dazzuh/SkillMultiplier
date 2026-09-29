param(
    [string]$SPTDir = 'S:\SPT-Dev\4.1',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# The live, heavily modded install must never be a build or deploy target.
if ($SPTDir -match 'SPT4\.1(?!\\)' -or $SPTDir -like '*\SPT4.1' -or $SPTDir -like '*\SPT4.1\*') {
    throw "Refusing to deploy to a live install: $SPTDir"
}

$repo = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $SPTDir 'SPT_Runtime'
$mods = Join-Path $runtime 'user\mods'

# A second install of this mod doubles every multiplier it applies: two server mods scaling the same globals,
# and two plugins both prefixing Skill.OnTrigger. Refuse rather than ship a silent 2x or 4x.
$stale = Get-ChildItem $mods -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'dazzuh-skillmultiplier' } |
    Where-Object { $_.Name -like '*SkillMultiplier*' }
if ($stale) {
    throw "Another install of this mod is present and would apply the same multipliers twice: $($stale.Name -join ', '). Remove it first."
}

# --- server half ------------------------------------------------------------

# The running server holds its own DLL and wwwroot, and it has to be restarted for the new globals to be
# re-read anyway - so stop it rather than fail on a locked file.
$server = Get-Process SPT.Server -ErrorAction SilentlyContinue
if ($server) {
    Write-Host 'Stopping SPT.Server (a restart is needed for the new values anyway)'
    $server | Stop-Process -Force
    # A new instance cannot bind the port until the old one has actually exited; starting it during
    # the race leaves the old process answering with the old assembly and no error anywhere.
    for ($i = 0; $i -lt 30 -and (Get-Process SPT.Server -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Seconds 1
    }
    if (Get-Process SPT.Server -ErrorAction SilentlyContinue) {
        throw 'SPT.Server did not stop; refusing to deploy under a running server.'
    }
}

$proj = Join-Path $repo 'SkillMultiplier-server\SkillMultiplier-server.csproj'
Write-Host "Building $proj ($Configuration)"
dotnet build $proj -c $Configuration -p:SPTRuntimeDir="$runtime"
if ($LASTEXITCODE -ne 0) { throw 'server build failed' }

$out = Join-Path $repo "SkillMultiplier-server\bin\$Configuration\SkillMultiplier-server"
$dll = Join-Path $out 'SkillMultiplier-server.dll'
if (-not (Test-Path $dll)) { throw "no built assembly at $dll" }

# A copied-in game assembly would shadow the real one. Reference Private=false should prevent it;
# this is the check that proves it, because "green build" is not "clean output".
$strays = Get-ChildItem -Path $out -Filter *.dll |
    Where-Object { $_.Name -notlike 'SkillMultiplier*' }
if ($strays) {
    throw "build output contains stray assemblies that must not ship: $($strays.Name -join ', ')"
}

# v1's release installed to this exact folder name, so keeping it means an update lands on the folder that
# already exists instead of sitting beside it as a second copy.
$dest = Join-Path $mods 'dazzuh-skillmultiplier'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Copy-Item $dll (Join-Path $dest 'SkillMultiplier-server.dll') -Force
Copy-Item (Join-Path $out 'wwwroot') $dest -Recurse -Force

# Same reasoning as the client half below: prove what landed, do not assume it.
if ((Get-FileHash $dll).Hash -ne (Get-FileHash (Join-Path $dest 'SkillMultiplier-server.dll')).Hash) {
    throw "The deployed server assembly is not the one just built: $dest\SkillMultiplier-server.dll"
}

# config.json is the user's file. Only seed it when absent, so a deploy never wipes their tuning - and on
# first load the server migrates the previous release's fields out of it.
$cfg = Join-Path $dest 'config.json'
if (-not (Test-Path $cfg)) {
    Copy-Item (Join-Path $out 'config.json') $cfg
    Write-Host 'Seeded config.json (no existing config found)'
} else {
    Write-Host 'Kept existing config.json'
}

Write-Host "Deployed the server half to $dest"

# --- client half ------------------------------------------------------------
# Both halves ship together: server-side scaling cannot reach the per-action values the client hardcodes,
# and a client without a server has no table to apply.

$clientProj = Join-Path $repo 'src\SkillMultiplier.csproj'
Write-Host "Building $clientProj ($Configuration)"
dotnet build $clientProj -c $Configuration -p:SPTDir="$SPTDir"
if ($LASTEXITCODE -ne 0) { throw 'client build failed' }

$clientDll = Join-Path $repo "src\bin\$Configuration\netstandard2.1\dazzuh.skillmultiplier.dll"
if (-not (Test-Path $clientDll)) { throw "no built client at $clientDll" }

$clientDest = Join-Path $SPTDir 'BepInEx\plugins'
try {
    Copy-Item $clientDll $clientDest -Force
} catch {
    throw "Could not replace the client plugin - the game is probably running and holding the file. Close it and deploy again. ($($_.Exception.Message))"
}

# "Deployed" is a claim about the bytes on disk, not about the build succeeding. A running game holding the
# plugin has shipped the previous build through this script before, and the only thing that caught it was
# comparing hashes afterwards - so do that here rather than relying on the copy not throwing.
$shippedClient = Join-Path $clientDest 'dazzuh.skillmultiplier.dll'
if ((Get-FileHash $clientDll).Hash -ne (Get-FileHash $shippedClient).Hash) {
    throw "The deployed client plugin is not the one just built: $shippedClient. Close the game and deploy again."
}

Write-Host "Deployed the client half to $clientDest"
Write-Host ''
Write-Host 'UI: /skillmultiplier/index.html on the SPT server port (see SPT_Data\configs\http.json).'
Write-Host 'NOTE: the server was stopped for this deploy - start SPT.Server.exe again to re-read its globals.'
Write-Host '      Multiplier changes are pushed to a running client over its socket, so a game restart is'
Write-Host '      only needed for the values the client reads once, at its own startup.'
