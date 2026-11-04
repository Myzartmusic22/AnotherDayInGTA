#requires -version 5.1
<#
    Another Day in GTA - installer (Windows PowerShell 5.1+)

    Builds the plugin and the AI service, then installs them into the GTA V
    Legacy folder and the per-user data directory.

    - Never modifies GTA installation files other than adding this mod's own
      plugin DLLs to <game>\scripts.
    - Backs up any file it is about to overwrite (<name>.bak-<timestamp>).
    - Preserves the user database, config, IPC token and audio cache.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\install.ps1
        powershell -ExecutionPolicy Bypass -File tools\install.ps1 -GamePath "D:\Games\GTAV" -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$GamePath = "F:\SteamLibrary\steamapps\common\Grand Theft Auto V",
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "AnotherDayInGTA"),
    [switch]$SkipBuild,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

# ---------------------------------------------------------------- prerequisites
if (-not (Test-Path (Join-Path $GamePath "GTA5.exe"))) {
    throw "GTA5.exe not found in '$GamePath'. Pass -GamePath with your GTA V Legacy folder."
}
if (-not (Test-Path (Join-Path $GamePath "ScriptHookVDotNet3.dll"))) {
    throw "ScriptHookVDotNet3.dll is missing from '$GamePath'. Install ScriptHookV + ScriptHookVDotNet 3.7 (nightly for game build 3889) first."
}
if (-not (Test-Path (Join-Path $GamePath "ScriptHookVDotNet-Loader.asi"))) {
    Write-Warn2 "ScriptHookVDotNet-Loader.asi is missing; SHVDN versions 3.7+ need it. Install the matching SHVDN release."
}

$scripts = Join-Path $GamePath "scripts"
if (-not (Test-Path $scripts)) { New-Item -ItemType Directory -Path $scripts | Out-Null }

# ---------------------------------------------------------------------- build
if (-not $SkipBuild) {
    Write-Step "Building plugin (Release, net48)"
    & dotnet build (Join-Path $repo "src\AnotherDay.Plugin\AnotherDay.Plugin.csproj") -c Release -v m
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

    Write-Step "Publishing AI service"
    $publishArgs = @(
        "publish", (Join-Path $repo "src\AnotherDay.Service\AnotherDay.Service.csproj"),
        "-c", "Release", "-r", "win-x64", "-o", (Join-Path $repo "artifacts\service")
    )
    if ($FrameworkDependent) { $publishArgs += @("--self-contained", "false") }
    else { $publishArgs += @("--self-contained", "true") }
    & dotnet @publishArgs -v m
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }
}

# AppendTargetFrameworkToOutputPath=false keeps the net48 output directly in bin\Release.
$pluginOut = Join-Path $repo "src\AnotherDay.Plugin\bin\Release"
$serviceOut = Join-Path $repo "artifacts\service"
if (-not (Test-Path (Join-Path $pluginOut "AnotherDay.Plugin.dll"))) { throw "Plugin output missing at $pluginOut." }
if (-not (Test-Path (Join-Path $serviceOut "AnotherDay.Service.exe"))) { throw "Service output missing at $serviceOut." }

# --------------------------------------------------------------- install plugin
Write-Step "Installing plugin into $scripts"
$pluginFiles = @(
    "AnotherDay.Plugin.dll",
    "AnotherDay.Shared.dll",
    "Newtonsoft.Json.dll",
    "NAudio.dll", "NAudio.Core.dll", "NAudio.WinMM.dll", "NAudio.Wasapi.dll"
)
foreach ($file in $pluginFiles) {
    $source = Join-Path $pluginOut $file
    if (-not (Test-Path $source)) { Write-Warn2 "Skipping missing $file"; continue }
    $destination = Join-Path $scripts $file
    if (Test-Path $destination) {
        Copy-Item $destination "$destination.bak-$stamp" -Force
    }
    Copy-Item $source $destination -Force
    Write-Ok $file
}

# -------------------------------------------------------------- install service
Write-Step "Installing AI service into $DataDir\service"
# A service started by the game holds its own DLLs open; stop it before copying.
$runningService = Get-Process -Name "AnotherDay.Service" -ErrorAction SilentlyContinue
if ($runningService) {
    Write-Warn2 "Stopping the running AI service (PID $($runningService.Id)) so its files can be replaced"
    $runningService | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}
$serviceDir = Join-Path $DataDir "service"
New-Item -ItemType Directory -Path $serviceDir -Force | Out-Null
Get-ChildItem $serviceDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "*.json" -and $_.Name -notlike "*.db*" } |
    Remove-Item -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $serviceOut "*") $serviceDir -Recurse -Force
Write-Ok "$((Get-ChildItem $serviceDir -File).Count) service files"

# ------------------------------------------------------------------- data files
Write-Step "Preparing data directory $DataDir"
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
foreach ($sub in @("logs", "audio_cache", "backups", "crashes")) {
    New-Item -ItemType Directory -Path (Join-Path $DataDir $sub) -Force | Out-Null
}

$pluginConfig = Join-Path $scripts "AnotherDay.Plugin.json"
if (-not (Test-Path $pluginConfig)) {
    $json = [ordered]@{
        dataDirectory         = $DataDir
        pipeName              = "AnotherDayInGTA"
        autoStartService      = $true
        serviceExePath        = ""
        voiceKey              = "P"
        socialKey             = "Alt+Shift+7"
        statusKey             = "Alt+Shift+6"
        conversationRangeMeters = 30
        micDevice             = ""
        bargeInThreshold      = 0.02
        snapshotIntervalMs    = 1200
        maxNearbyPeds         = 48
        volume                = 1.0
        showHud               = $true
        debug                 = $false
    }
    $json | ConvertTo-Json | Set-Content -Path $pluginConfig -Encoding UTF8
    Write-Ok "Wrote default AnotherDay.Plugin.json"
} else {
    Write-Ok "Kept existing AnotherDay.Plugin.json"
}

Write-Step "Checking .NET runtime"
try {
    $versions = & dotnet --list-runtimes 2>$null
    if ($versions -match "Microsoft.NETCore.App 10\.") { Write-Ok ".NET 10 runtime present" }
    else { Write-Warn2 ".NET 10 desktop runtime not detected. Install 'Microsoft .NET Runtime 10' or rerun the installer without -FrameworkDependent." }
} catch {
    Write-Warn2 "dotnet CLI not found; the self-contained service build still runs without it."
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host "  1. Launch GTA V Legacy (Story Mode)."
Write-Host "  2. Press P once near a pedestrian and speak naturally after connection. Hold P during an NPC reply to interrupt; O ends the conversation. Alt+Shift+7 starts a social scene."
Write-Host "  3. Set a Gemini key with:  `"$serviceDir\AnotherDay.Service.exe`" --set-key"
Write-Host "  4. Logs: $DataDir\logs   Database: $DataDir\anotherday.db"
