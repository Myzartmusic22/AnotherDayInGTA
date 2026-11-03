#requires -version 5.1
<#
    Another Day in GTA - uninstaller.

    Removes the plugin DLLs from <game>\scripts and (only with -RemoveData) the
    per-user data directory. The GTA installation itself is never modified.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1
        powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string]$GamePath = "F:\SteamLibrary\steamapps\common\Grand Theft Auto V",
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "AnotherDayInGTA"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

$scripts = Join-Path $GamePath "scripts"
$pluginFiles = @(
    "AnotherDay.Plugin.dll",
    "AnotherDay.Shared.dll",
    "AnotherDay.Plugin.json"
)

Write-Host "==> Removing plugin files from $scripts"
foreach ($file in $pluginFiles) {
    $path = Join-Path $scripts $file
    if (Test-Path $path) {
        Remove-Item $path -Force
        Write-Host "    removed $file" -ForegroundColor Green
    }
}

Write-Host "==> Stopping the AI service"
Get-Process -Name "AnotherDay.Service" -ErrorAction SilentlyContinue | Stop-Process -Force

$serviceDir = Join-Path $DataDir "service"
if (Test-Path $serviceDir) {
    Remove-Item $serviceDir -Recurse -Force
    Write-Host "    removed service binaries" -ForegroundColor Green
}

# Shared libraries are left in place: other mods may use Newtonsoft.Json/NAudio.
Write-Host "    kept shared Newtonsoft.Json.dll / NAudio*.dll (used by other mods)" -ForegroundColor Yellow

if ($RemoveData) {
    if (Test-Path $DataDir) {
        Remove-Item $DataDir -Recurse -Force
        Write-Host "    removed user data at $DataDir" -ForegroundColor Green
    }
} else {
    Write-Host "    kept user data at $DataDir (database, memories, voices)" -ForegroundColor Yellow
    Write-Host "    rerun with -RemoveData to delete it" -ForegroundColor Yellow
}

Write-Host "Uninstall complete." -ForegroundColor Green
