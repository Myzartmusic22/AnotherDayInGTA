#requires -version 5.1
<#
    Another Day in GTA - pre-flight check.

    Verifies every prerequisite the in-game checklist depends on BEFORE the game
    is launched: game build, ScriptHookV, the ScriptHookVDotNet loader, the
    plugin files, the AI service install and the data directory.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\preflight.ps1
        powershell -ExecutionPolicy Bypass -File tools\preflight.ps1 -GameDir "D:\Games\GTAV"
#>
[CmdletBinding()]
param(
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\Grand Theft Auto V",
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "AnotherDayInGTA")
)

$ErrorActionPreference = "Continue"
$script:Failures = 0
$script:Warnings = 0

function Pass([string]$name, [string]$detail) {
    Write-Host ("  [ OK ] {0,-34} {1}" -f $name, $detail) -ForegroundColor Green
}
function Fail([string]$name, [string]$detail) {
    $script:Failures++
    Write-Host ("  [FAIL] {0,-34} {1}" -f $name, $detail) -ForegroundColor Red
}
function Warn([string]$name, [string]$detail) {
    $script:Warnings++
    Write-Host ("  [WARN] {0,-34} {1}" -f $name, $detail) -ForegroundColor Yellow
}
function Section([string]$title) {
    Write-Host ""
    Write-Host "==> $title" -ForegroundColor Cyan
}

function Get-FileVersion([string]$path) {
    try { return (Get-Item $path).VersionInfo.FileVersion } catch { return $null }
}

function Get-LastMatch([string]$path, [string]$pattern) {
    if (-not (Test-Path $path)) { return $null }
    $hit = Select-String -Path $path -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($hit) { return $hit.Line.Trim() }
    return $null
}

Write-Host "Another Day in GTA - pre-flight" -ForegroundColor White
Write-Host "  game: $GameDir"
Write-Host "  data: $DataDir"

Section "Game and hook"
if (Test-Path (Join-Path $GameDir "GTA5.exe")) {
    $exe = Join-Path $GameDir "GTA5.exe"
    $ver = Get-FileVersion $exe
    Pass "GTA5.exe" ($(if ($ver) { "file version $ver" } else { "found" }))
} else {
    Fail "GTA5.exe" "not found - check -GameDir"
}

$shv = Join-Path $GameDir "ScriptHookV.dll"
if (Test-Path $shv) {
    $shvVer = Get-FileVersion $shv
    Pass "ScriptHookV.dll" ($(if ($shvVer) { "v$shvVer" } else { "found" }))
} else {
    Fail "ScriptHookV.dll" "missing - the plugin cannot load without it"
}

if ((Test-Path (Join-Path $GameDir "dinput8.dll")) -or (Test-Path (Join-Path $GameDir "dsound.dll"))) {
    Pass "ASI loader" "dinput8/dsound present"
} else {
    Fail "ASI loader" "no dinput8.dll / dsound.dll - .asi files will not load"
}

Section "ScriptHookVDotNet"
$loader = $null
foreach ($candidate in @("ScriptHookVDotNet-Loader.asi", "ScriptHookVDotNet.asi")) {
    $p = Join-Path $GameDir $candidate
    if (Test-Path $p) { $loader = $p; break }
}
if ($loader) {
    Pass "SHVDN loader" (Split-Path $loader -Leaf)
} else {
    Fail "SHVDN loader" "no ScriptHookVDotNet-Loader.asi / ScriptHookVDotNet.asi in the game root"
}

$core = Join-Path $GameDir "ScriptHookVDotNet3.dll"
if (Test-Path $core) {
    Pass "ScriptHookVDotNet3.dll" ("v" + (Get-FileVersion $core))
} else {
    Fail "ScriptHookVDotNet3.dll" "missing - this is the API the plugin compiles against"
}

$shvLog = Join-Path $GameDir "ScriptHookV.log"
if (Test-Path $shvLog) {
    $init = Get-LastMatch $shvLog "INIT: Success, game version is"
    if ($init) { Pass "ScriptHookV last init" $init } else { Warn "ScriptHookV last init" "no successful init line yet" }
    $registered = Select-String -Path $shvLog -Pattern "Registering script 'ScriptHookVDotNet" -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($registered) {
        Pass "SHVDN registered by SHV" $registered.Line.Trim()
    } else {
        Warn "SHVDN registered by SHV" "not in ScriptHookV.log yet - launch the game once after installing"
    }
} else {
    Warn "ScriptHookV.log" "not present - game has not been launched since ScriptHookV was installed"
}

Section "Plugin files"
$scripts = Join-Path $GameDir "scripts"
$required = @(
    "AnotherDay.Plugin.dll",
    "AnotherDay.Shared.dll",
    "Newtonsoft.Json.dll",
    "NAudio.dll",
    "NAudio.Core.dll",
    "NAudio.WinMM.dll",
    "NAudio.Wasapi.dll"
)
foreach ($file in $required) {
    $p = Join-Path $scripts $file
    if (Test-Path $p) {
        Pass $file ((Get-Item $p).LastWriteTime.ToString("yyyy-MM-dd HH:mm"))
    } else {
        Fail $file "missing from scripts\\ - re-run tools\\install.ps1"
    }
}

$configPath = Join-Path $scripts "AnotherDay.Plugin.json"
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        Pass "AnotherDay.Plugin.json" ("pipe=" + $cfg.pipeName + " service=" + $(if ($cfg.autoStartService) { "auto" } else { "manual" }))
        if ($cfg.dataDirectory -and ($cfg.dataDirectory.TrimEnd('\') -ne $DataDir.TrimEnd('\'))) {
            Warn "plugin data directory" ("plugin uses " + $cfg.dataDirectory + ", this check used " + $DataDir)
        }
        if (-not $cfg.autoStartService -and -not (Get-Process -Name "AnotherDay.Service" -ErrorAction SilentlyContinue)) {
            Warn "service autostart" "disabled and no service process is running - the plugin will report offline"
        }
    } catch {
        Fail "AnotherDay.Plugin.json" ("unreadable JSON: " + $_.Exception.Message)
    }
} else {
    Fail "AnotherDay.Plugin.json" "missing - the plugin will use defaults but will not be configured"
}

$foreign = Get-ChildItem -Path $scripts -Filter "*.dll" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin $required }
if ($foreign) {
    Warn "other script DLLs" (($foreign | Select-Object -First 6 | ForEach-Object { $_.Name }) -join ", ")
}

Section "AI service"
$serviceExe = Join-Path $DataDir "service\AnotherDay.Service.exe"
if (Test-Path $serviceExe) {
    Pass "AnotherDay.Service.exe" ("v" + (Get-FileVersion $serviceExe))
} else {
    Fail "AnotherDay.Service.exe" "not installed - run tools\install.ps1"
}

$runtime = $null
try { $runtime = & dotnet --list-runtimes 2>$null | Select-String "Microsoft.NETCore.App 10\." | Select-Object -First 1 } catch { }
if ($runtime) {
    Pass "dotnet runtime" $runtime.Line.Trim()
} else {
    Warn "dotnet runtime" "Microsoft.NETCore.App 10.x not listed - a self-contained build avoids this"
}

$running = Get-Process -Name "AnotherDay.Service" -ErrorAction SilentlyContinue
if ($running) {
    Pass "service process" ("running (pid " + (($running | Select-Object -First 1).Id) + ")")
} else {
    Pass "service process" "not running (autostart handles it on game launch)"
}

Section "Data directory"
if (Test-Path $DataDir) {
    Pass "data directory" $DataDir
} else {
    Warn "data directory" "does not exist yet - created on first service start"
}

$token = Join-Path $DataDir "ipc.token"
$tokenInfo = New-Object System.IO.FileInfo($token)
if ($tokenInfo.Exists) {
    if ($tokenInfo.Length -gt 0) {
        Pass "ipc.token" ("present, " + $tokenInfo.Length + " bytes")
    } else {
        Warn "ipc.token" "empty - the service rewrites it on its next start; the plugin re-reads it then"
    }
} else {
    Warn "ipc.token" "missing - created when the service first starts"
}

$db = Join-Path $DataDir "anotherday.db"
if (Test-Path $db) {
    $wal = Join-Path $DataDir "anotherday.db-wal"
    $walSize = 0
    if (Test-Path $wal) { $walSize = [math]::Round((Get-Item $wal).Length / 1KB) }
    Pass "database" ("anotherday.db " + [math]::Round((Get-Item $db).Length / 1KB) + " KB, wal " + $walSize + " KB")
} else {
    Warn "database" "not created yet - the service builds it on first start"
}

$logDir = Join-Path $DataDir "logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$probe = Join-Path $logDir ".write-probe"
try {
    "ok" | Out-File -FilePath $probe -Encoding ASCII -Force
    Remove-Item $probe -Force
    Pass "log directory writable" $logDir
} catch {
    Fail "log directory writable" ("cannot write to " + $logDir + ": " + $_.Exception.Message)
}

$keyConfigured = $false
$serviceLogDir = $logDir
if (Test-Path $serviceLogDir) {
    $latest = Get-ChildItem $serviceLogDir -Filter "service-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($latest) {
        $keyLine = Select-String -Path $latest.FullName -Pattern 'Gemini: ' -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($keyLine -and ($keyLine.Line -notmatch 'not configured')) { $keyConfigured = $true }
    }
}
if ($keyConfigured) {
    Pass "Gemini key" "configured (per the last service log)"
} else {
    Warn "Gemini key" "not configured - conversations and voices run in offline mode"
}

Write-Host ""
if ($script:Failures -eq 0) {
    Write-Host ("PREFLIGHT PASSED ({0} warning(s))" -f $script:Warnings) -ForegroundColor Green
    Write-Host "Launch Story Mode, then run tools\diagnose.ps1 after the session."
    exit 0
}
Write-Host ("PREFLIGHT FAILED ({0} failure(s), {1} warning(s))" -f $script:Failures, $script:Warnings) -ForegroundColor Red
exit 1
