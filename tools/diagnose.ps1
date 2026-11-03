#requires -version 5.1
<#
    Another Day in GTA - session diagnosis.

    Reads the plugin log, the AI service log and ScriptHookV.log from the most
    recent session, prints the meaningful events, maps them to the manual
    checklist in docs\TESTING.md, and lists concrete fixes for anything that
    looks broken.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1
        powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1 -All
        powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1 -GameDir "D:\Games\GTAV"
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "AnotherDayInGTA"),
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\Grand Theft Auto V",
    [switch]$All
)

$ErrorActionPreference = "Continue"
$logDir = Join-Path $DataDir "logs"
$script:Findings = New-Object System.Collections.ArrayList

function Finding([string]$severity, [string]$title, [string]$detail, [string]$fix) {
    [void]$script:Findings.Add([pscustomobject]@{
        Severity = $severity; Title = $title; Detail = $detail; Fix = $fix
    })
}

function Get-Lines([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content -Path $path -ErrorAction SilentlyContinue)
}

function Get-LatestLog([string]$pattern) {
    $files = Get-ChildItem -Path $logDir -Filter $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
    if (-not $files) { return $null }
    if ($All) { return $files }
    return @($files | Select-Object -Last 1)
}

function Strip-Ts([string]$line) {
    # service log lines are JSON; plugin lines are "date time [LEVEL] message"
    if ($line -match '^\{"ts":"([^"]+)"') {
        $ts = $Matches[1]
        $msg = $null
        try {
            $obj = $line | ConvertFrom-Json
            $msg = $obj.msg
            if ($obj.level) { return [pscustomobject]@{ Ts = $ts; Level = $obj.level; Msg = $msg } }
        } catch { }
        return [pscustomobject]@{ Ts = $ts; Level = "info"; Msg = $line }
    }
    if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)\s+\[(\w+)\]\s+(.*)$') {
        return [pscustomobject]@{ Ts = $Matches[1]; Level = $Matches[2].ToLower(); Msg = $Matches[3] }
    }
    return [pscustomobject]@{ Ts = ""; Level = "info"; Msg = $line }
}

Write-Host "Another Day in GTA - session diagnosis" -ForegroundColor White
Write-Host "  data: $DataDir"

# ---------------------------------------------------------------- plugin log
$pluginFiles = Get-LatestLog "plugin-*.log"
$plugin = @()
if ($pluginFiles) {
    foreach ($f in $pluginFiles) {
        Write-Host ("  plugin log: {0}  ({1:yyyy-MM-dd HH:mm}, {2} lines)" -f $f.Name, $f.LastWriteTime, (Get-Content $f.FullName).Count)
        $plugin += Get-Lines $f.FullName | ForEach-Object { Strip-Ts $_ }
    }
} else {
    Write-Host "  plugin log: none" -ForegroundColor Yellow
}

# --------------------------------------------------------------- service log
$serviceFiles = Get-LatestLog "service-*.log"
$service = @()
if ($serviceFiles) {
    foreach ($f in $serviceFiles) {
        Write-Host ("  service log: {0}  ({1:yyyy-MM-dd HH:mm}, {2} lines)" -f $f.Name, $f.LastWriteTime, (Get-Content $f.FullName).Count)
        $service += Get-Lines $f.FullName | ForEach-Object { Strip-Ts $_ }
    }
} else {
    Write-Host "  service log: none" -ForegroundColor Yellow
}

# Scope the service log to this game session: the service log accumulates across
# runs, so without this the coverage table would credit earlier sessions.
$sessionStartUtc = $null
if ($plugin -and $plugin.Count -gt 0) {
    $firstTs = @($plugin | Where-Object { $_.Ts } | Select-Object -First 1)
    if ($firstTs.Count -gt 0) {
        try {
            $sessionStartUtc = ([datetime]::Parse($firstTs[0].Ts, [Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime()
        } catch { }
    }
}
if ($sessionStartUtc) {
    $scoped = @($service | Where-Object {
        if (-not $_.Ts) { return $true }
        try { return ([datetime]::Parse($_.Ts, [Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime() -ge $sessionStartUtc.AddMinutes(-2) }
        catch { return $true }
    })
    $dropped = $service.Count - $scoped.Count
    if ($dropped -gt 0) {
        Write-Host ("  service log scoped to this session: {0} of {1} line(s) before {2:HH:mm} ignored" -f $scoped.Count, $service.Count, $sessionStartUtc.ToLocalTime())
    }
    $service = $scoped
}

function Count-Matching($rows, [string]$pattern) {
    if (-not $rows) { return 0 }
    return @($rows | Where-Object { $_.Msg -match $pattern }).Count
}
function First-Matching($rows, [string]$pattern) {
    if (-not $rows) { return $null }
    return @($rows | Where-Object { $_.Msg -match $pattern }) | Select-Object -First 1
}

# ------------------------------------------------------------------ findings
$pluginLoaded = Count-Matching $plugin "plugin loaded"
$connected = Count-Matching $plugin "Connected to AI service"
$unloaded = Count-Matching $plugin "plugin unloaded"
$newIdentities = Count-Matching $service "New identity "
$adopted = Count-Matching $service "Adopted "
$conversations = Count-Matching $service "^Conversation with "
$social = Count-Matching $service "Social conversation "
$geminiConfigured = $false
if (@($service | Where-Object { $_.Msg -match 'Gemini: configured' -and $_.Msg -notmatch 'not configured' }).Count -gt 0) { $geminiConfigured = $true }

if (-not $pluginFiles) {
    Finding "critical" "The plugin never wrote a log" `
        "No plugin-*.log in $logDir. The plugin never ran, so ScriptHookVDotNet did not load it." `
        "Run tools\preflight.ps1. Then check <game>\ScriptHookV.log for ""Registering script 'ScriptHookVDotNet"". If it is absent, the loader .asi is not installed or ScriptHookV did not start."
} elseif ($pluginLoaded -eq 0) {
    Finding "critical" "Plugin log exists but reports no load" `
        "plugin-*.log has no 'Another Day in GTA plugin loaded.' line." `
        "Look for 'Plugin failed to initialize' in the same log; that carries the exception that stopped the constructor."
} else {
    if ($connected -eq 0) {
        Finding "critical" "Plugin could not reach the AI service" `
            "No 'Connected to AI service' line after $pluginLoaded load(s)." `
            "Check the service log for 'IPC listening'. Confirm %LOCALAPPDATA%\AnotherDayInGTA\service\AnotherDay.Service.exe exists, then run tools\smoke-test.ps1 to prove the pipe works outside the game."
    }
    if ($unloaded -eq 0 -and $connected -gt 0) {
        Finding "warn" "No clean unload recorded" `
            "The plugin logged a connection but never 'plugin unloaded'. The game may have been closed abruptly or crashed." `
            "Check for dumps in $DataDir\crashes and look at the last plugin lines below."
    }
}

if ((Count-Matching $service "Another service instance is already running") -gt 0) {
    Finding "warn" "Duplicate service start" `
        "A second service process started and exited because the pipe was already owned." `
        "Harmless, but if it repeats, set autoStartService=false in scripts\AnotherDay.Plugin.json and start the service once yourself."
}

if ((Count-Matching $service "Database unavailable; running in memory-only mode") -gt 0) {
    Finding "critical" "Database unavailable - memories will not persist" `
        "The service fell back to an in-memory store." `
        "Close GTA, then check $DataDir\anotherday.db and the -wal file for a lock held by another process. Backups are in $DataDir\backups."
}
if ((Count-Matching $service "Moved unreadable database") -gt 0) {
    Finding "warn" "Corrupt database was quarantined" `
        "The service found the database unreadable, moved it aside and created a fresh one." `
        "Old memories are in $DataDir\backups. Do not delete the quarantined file until you have decided whether you need it."
}

if (-not $geminiConfigured) {
    Finding "info" "Gemini is not configured" `
        "Conversations, voices and autonomous chatter are using the offline provider." `
        "Run: & ""$DataDir\service\AnotherDay.Service.exe"" --set-key"
}

$geminiFails = Count-Matching $service "Gemini conversation failed|Gemini group conversation failed"
if ($geminiFails -gt 0) {
    Finding "warn" "Gemini calls failed ($geminiFails)" `
        "The service fell back to local behavior after an API error." `
        "Read the 'Gemini <purpose> HTTP <status>' lines below: 400/403 means a bad key or model name, 429 means quota, timeouts mean network."
}

$ttsFails = Count-Matching $service "Gemini TTS failed|Speech synthesis failed"
if ($ttsFails -gt 0) {
    Finding "warn" "Voice synthesis failed ($ttsFails)" `
        "Replies came back as text without the assigned voice, or the fallback voice failed." `
        "Confirm the TTS model name in $DataDir\config.json under gemini.ttsModel. If no Windows voices are installed, the fallback path cannot speak."
}

if ((Count-Matching $plugin "Microphone stopped with an error") -gt 0) {
    Finding "warn" "Microphone failed" `
        "The recorder stopped with an error during a voice interaction." `
        "Check Windows privacy settings allow microphone access for desktop apps, and that the default recording device is enabled."
}

$actionFails = Count-Matching $plugin "Action .* failed"
if ($actionFails -gt 0) {
    Finding "warn" "Game actions failed ($actionFails)" `
        "An approved AI action could not be executed on the ped (dead, despawned, or blocked by a task)." `
        "Inspect the 'Action <name> failed' lines below. Actions are cancelled safely, so this is a fidelity issue rather than a stability one."
}

$snapFails = Count-Matching $plugin "Snapshot collection failed|Service rejected snapshot"
if ($snapFails -gt 0) {
    Finding "warn" "Ped snapshots failed ($snapFails)" `
        "The plugin could not build or deliver a snapshot, so identities stop being refreshed." `
        "If the message mentions the pipe, the service died mid-session; see the service log tail."
}

if ($connected -gt 0 -and $newIdentities -eq 0 -and $adopted -eq 0) {
    Finding "warn" "No identities were created" `
        "The service never logged 'New identity', so no pedestrian was bound. HUD counts would stay at zero." `
        "Stand still in a populated area for ~10 seconds. If it persists, set ""debug"": true in scripts\AnotherDay.Plugin.json and check 'Snapshot collection' lines."
}

if ($pluginLoaded -gt 0 -and $newIdentities -gt 0 -and $conversations -eq 0) {
    Finding "info" "Identities exist but no player conversation was recorded" `
        "$newIdentities identities were bound in this session, zero conversations." `
        "Target a ped within range and press F8 to exercise checklist items 3-5."
}

if ($connected -gt 0 -and $conversations -eq 0 -and $social -eq 0) {
    Finding "info" "No autonomous chatter yet" `
        "The social engine logged no conversations." `
        "Checklist item 9 needs two peds within a few metres (F10 forces a scene) and background chatter is rate-limited by config."
}

# ------------------------------------------------------------------ coverage
Write-Host ""
Write-Host "==> Checklist coverage from this session" -ForegroundColor Cyan
$coverage = @(
    @{ N = 1;  Name = "Plugin loads";              Ok = ($pluginLoaded -gt 0) },
    @{ N = 2;  Name = "Peds identified";           Ok = (($newIdentities + $adopted) -gt 0) },
    @{ N = 3;  Name = "Player conversation";       Ok = ($conversations -gt 0) },
    @{ N = 4;  Name = "Distinct voices per NPC";   Ok = (@($service | Where-Object { $_.Msg -match 'Assigned voice' }).Count -gt 1) },
    @{ N = 6;  Name = "Voice generated for reply";  Ok = (@($service | Where-Object { $_.Msg -match 'voice=(?!none)' }).Count -gt 0) },
    @{ N = 8;  Name = "Identity across sessions";  Ok = ($adopted -gt 0) },
    @{ N = 9;  Name = "NPC-to-NPC conversation";   Ok = ($social -gt 0) },
    @{ N = 12; Name = "Offline fallback worked";   Ok = ($conversations -gt 0 -and -not $geminiConfigured) },
    @{ N = 14; Name = "Data dir populated";        Ok = (Test-Path (Join-Path $DataDir "anotherday.db")) }
)
foreach ($item in $coverage) {
    $mark = if ($item.Ok) { "evidence" } else { "no evidence" }
    $colour = if ($item.Ok) { "Green" } else { "DarkGray" }
    Write-Host ("  {0,2}. {1,-32} {2}" -f $item.N, $item.Name, $mark) -ForegroundColor $colour
}
Write-Host "  (items 5, 7, 10, 11, 13 are judgement or audible/visual checks - judge them in game)"

# ------------------------------------------------------------------ timeline
Write-Host ""
Write-Host "==> Notable plugin events" -ForegroundColor Cyan
$interesting = @($plugin | Where-Object {
    $_.Msg -match "plugin loaded|Connected to AI service|Started AI service|plugin unloaded|failed|error|Heard:"
})
if ($interesting.Count -eq 0) { Write-Host "  (nothing)" -ForegroundColor DarkGray }
else {
    $interesting | Select-Object -Last 25 | ForEach-Object {
        $colour = if ($_.Level -in @("error", "warn")) { "Yellow" } else { "Gray" }
        Write-Host ("  {0} {1}" -f $_.Ts, $_.Msg) -ForegroundColor $colour
    }
}

Write-Host ""
Write-Host "==> Notable service events" -ForegroundColor Cyan
$serviceInteresting = @($service | Where-Object {
    $_.Msg -match "service .* starting|IPC listening|Game connected|Game disconnected|New identity|Adopted|Assigned voice|^Conversation with|Social conversation|Gemini:|Database ready|migrat|Shutdown requested|Service stopped|Fatal|unavailable"
})
if ($serviceInteresting.Count -eq 0) { Write-Host "  (nothing)" -ForegroundColor DarkGray }
else {
    $serviceInteresting | Select-Object -Last 40 | ForEach-Object {
        $colour = if ($_.Level -in @("error", "warn")) { "Yellow" } else { "Gray" }
        Write-Host ("  {0} {1}" -f $_.Ts, $_.Msg) -ForegroundColor $colour
    }
}

$errors = @($plugin + $service | Where-Object { $_.Level -eq "error" })
if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "==> Errors ($($errors.Count))" -ForegroundColor Red
    $errors | Group-Object { $_.Msg } | Sort-Object Count -Descending | Select-Object -First 12 | ForEach-Object {
        Write-Host ("  x{0}  {1}" -f $_.Count, $_.Name) -ForegroundColor Red
    }
}

$warns = @($plugin + $service | Where-Object { $_.Level -eq "warn" })
if ($warns.Count -gt 0) {
    Write-Host ""
    Write-Host "==> Warnings grouped ($($warns.Count))" -ForegroundColor Yellow
    $warns | ForEach-Object { $_.Msg -replace "\d+", "N" } | Group-Object | Sort-Object Count -Descending |
        Select-Object -First 10 | ForEach-Object { Write-Host ("  x{0}  {1}" -f $_.Count, $_.Name) -ForegroundColor Yellow }
}

# -------------------------------------------------------------- game hooks
Write-Host ""
Write-Host "==> ScriptHookV" -ForegroundColor Cyan
$shvLog = Join-Path $GameDir "ScriptHookV.log"
if (Test-Path $shvLog) {
    $shvLines = Get-Content $shvLog
    $init = @($shvLines | Select-String "INIT: Success, game version is" | Select-Object -Last 1)
    if ($init) { Write-Host ("  " + $init[0].Line.Trim()) }
    $shvdn = @($shvLines | Select-String "ScriptHookVDotNet" | Select-Object -Last 3)
    if ($shvdn.Count -gt 0) { $shvdn | ForEach-Object { Write-Host ("  " + $_.Line.Trim()) } }
    else { Write-Host "  ScriptHookV never registered a ScriptHookVDotNet loader in this log." -ForegroundColor Yellow }
    $crit = @($shvLines | Select-String "ERR|CRITICAL|exception" | Select-Object -Last 5)
    foreach ($c in $crit) { Write-Host ("  " + $c.Line.Trim()) -ForegroundColor Yellow }
} else {
    Write-Host "  ScriptHookV.log not found." -ForegroundColor Yellow
}

$shvdnLog = Join-Path $GameDir "ScriptHookVDotNet.log"
if (Test-Path $shvdnLog) {
    Write-Host ""
    Write-Host "==> ScriptHookVDotNet log (tail)" -ForegroundColor Cyan
    Get-Content $shvdnLog | Select-Object -Last 20 | ForEach-Object { Write-Host ("  " + $_) }
} else {
    Write-Host "  ScriptHookVDotNet.log not found - it is created on the first run that loads SHVDN." -ForegroundColor Yellow
}

$crashDir = Join-Path $DataDir "crashes"
# Other SHVDN mods can fail on their own (missing dependencies, API drift after a
# framework upgrade). Report them so they are not mistaken for this plugin.
if (Test-Path $shvdnLog) {
    $broken = @()
    foreach ($line in (Get-Content $shvdnLog -ErrorAction SilentlyContinue)) {
        if ($line -match "Failed to load script assembly (\S+)") { $broken += $Matches[1] }
        elseif ($line -match "Failed to instantiate script ([\w\.]+) ") { $broken += ($Matches[1] -split "\.")[0] }
    }
    $broken = @($broken | ForEach-Object { $_ -replace '\.dll:?$', '' -replace ':$', '' } |
        Where-Object { $_ -notmatch "^AnotherDay" } | Sort-Object -Unique)
    if ($broken.Count -gt 0) {
        Finding "warn" "Other ScriptHookVDotNet mods failed to load" `
            ("Not this mod, but these scripts threw during load: " + ($broken -join ", ")) `
            "Install the dependency each one names (for example LemonUI for EnableAllinteriors) or update that mod for the installed SHVDN version. This does not affect Another Day in GTA."
    }
}

if (Test-Path $crashDir) {
    $crashes = @(Get-ChildItem $crashDir -File -ErrorAction SilentlyContinue)
    if ($crashes.Count -gt 0) {
        Write-Host ""
        Write-Host ("==> Crash reports ({0})" -f $crashes.Count) -ForegroundColor Red
        $crashes | Sort-Object LastWriteTime | Select-Object -Last 5 | ForEach-Object {
            Write-Host ("  {0:yyyy-MM-dd HH:mm}  {1}" -f $_.LastWriteTime, $_.Name) -ForegroundColor Red
        }
        Finding "critical" "Crash reports present" `
            "$($crashes.Count) file(s) in $crashDir." `
            "Open the newest report and read the top frame; a native access violation inside a TASK_* call points at ActionExecutor."
    }
}

# ------------------------------------------------------------------ report
Write-Host ""
Write-Host "==> Findings" -ForegroundColor Cyan
if ($script:Findings.Count -eq 0) {
    Write-Host "  Nothing anomalous in the logs." -ForegroundColor Green
} else {
    foreach ($f in $script:Findings) {
        $colour = switch ($f.Severity) {
            "critical" { "Red" }
            "warn" { "Yellow" }
            default { "Gray" }
        }
        Write-Host ("  [{0}] {1}" -f $f.Severity.ToUpper(), $f.Title) -ForegroundColor $colour
        Write-Host ("         {0}" -f $f.Detail)
        Write-Host ("         fix: {0}" -f $f.Fix) -ForegroundColor DarkCyan
    }
}

$critical = @($script:Findings | Where-Object { $_.Severity -eq "critical" }).Count
Write-Host ""
if ($critical -gt 0) {
    Write-Host ("DIAGNOSIS: {0} critical issue(s)" -f $critical) -ForegroundColor Red
    exit 1
}
Write-Host "DIAGNOSIS: no critical issues" -ForegroundColor Green
exit 0
