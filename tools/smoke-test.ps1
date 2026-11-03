#requires -version 5.1
<#
    Another Day in GTA - IPC smoke test.

    Starts the installed AI service, performs the authenticated handshake,
    sends a two-pedestrian snapshot and one offline text conversation, then
    verifies the responses. Exits non-zero on failure.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "AnotherDayInGTA"),
    [string]$PipeName = "AnotherDayInGTA",
    [int]$StartupSeconds = 25
)

$ErrorActionPreference = "Stop"

function Send-Frame($stream, [string]$json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $header = [BitConverter]::GetBytes([int]$bytes.Length)
    $stream.Write($header, 0, 4)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Read-Frame($stream) {
    $header = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        $n = $stream.Read($header, $read, 4 - $read)
        if ($n -le 0) { throw "Service closed the pipe." }
        $read += $n
    }
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -le 0 -or $length -gt 16MB) { throw "Invalid frame length $length." }
    $payload = New-Object byte[] $length
    $read = 0
    while ($read -lt $length) {
        $n = $stream.Read($payload, $read, $length - $read)
        if ($n -le 0) { throw "Service closed the pipe during a frame." }
        $read += $n
    }
    return ([Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json)
}

function Invoke-Rpc($stream, [string]$type, $payload) {
    $envelope = @{
        schemaVersion = 1
        messageId     = [guid]::NewGuid().ToString("N")
        correlationId = [guid]::NewGuid().ToString("N")
        type          = $type
        sentUtc       = (Get-Date).ToUniversalTime().ToString("o")
        payloadJson   = ($payload | ConvertTo-Json -Depth 8 -Compress)
    } | ConvertTo-Json -Depth 4 -Compress
    Send-Frame $stream $envelope
    return Read-Frame $stream
}

$exe = Join-Path $DataDir "service\AnotherDay.Service.exe"
if (-not (Test-Path $exe)) { throw "Service not installed at $exe. Run tools\install.ps1 first." }

Write-Host "==> Stopping any running service"
Get-Process -Name "AnotherDay.Service" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host "==> Starting service"
$logPath = Join-Path $DataDir "logs\smoke-test.out.log"
$process = Start-Process -FilePath $exe -ArgumentList @("--data", "`"$DataDir`"", "--pipe", "`"$PipeName`"") `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput $logPath -RedirectStandardError "$logPath.err"

try {
    $tokenPath = Join-Path $DataDir "ipc.token"
    $deadline = (Get-Date).AddSeconds($StartupSeconds)
    while (-not (Test-Path $tokenPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $tokenPath)) { throw "The service did not create its IPC token within $StartupSeconds seconds. See $logPath." }
    if ($process.HasExited) { throw "The service exited with code $($process.ExitCode). See $logPath." }

    # Give the pipe listener a moment to accept connections.
    Start-Sleep -Milliseconds 800
    $token = (Get-Content $tokenPath -Raw).Trim()

    Write-Host "==> Handshake"
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(8000)
    try {
        $hello = Invoke-Rpc $pipe "hello" @{
            token = $token; clientVersion = "smoke"; gameVersion = "test"; protocolVersion = 1
        }
        if ($hello.type -ne "hello.result") { throw "Handshake failed: $($hello.payloadJson)" }
        $helloData = $hello.payloadJson | ConvertFrom-Json
        if (-not $helloData.ok) { throw "Handshake rejected: $($helloData.error)" }
        Write-Host "    connected, aiConfigured=$($helloData.aiConfigured)" -ForegroundColor Green

        Write-Host "==> Snapshot"
        $peds = @(
            @{ handle = 501; fingerprint = "a_m_y_business_01|m|4120|1"; modelHash = 4120; modelName = "a_m_y_business_01"; isMale = $true;
               categoryHint = "civilian"; position = @{ x = 1.5; y = 0.0; z = 0.0 }; distanceToPlayer = 1.5; health = 200; maxHealth = 200 },
            @{ handle = 502; fingerprint = "a_f_y_hipster_02|f|4120|2"; modelHash = 4120; modelName = "a_f_y_hipster_02"; isMale = $false;
               categoryHint = "civilian"; position = @{ x = 2.5; y = 0.0; z = 0.0 }; distanceToPlayer = 2.5; health = 200; maxHealth = 200 }
        )
        $snapshot = Invoke-Rpc $pipe "peds.snapshot" @{
            playerPosition = @{ x = 0.0; y = 0.0; z = 0.0 }; playerHandle = 1; zone = "Vinewood"; weather = "Clear";
            clockHour = 14; clockMinute = 0; peds = $peds; events = @()
        }
        if ($snapshot.type -ne "peds.snapshot.result") { throw "Snapshot failed: $($snapshot.payloadJson)" }
        $snapshotData = $snapshot.payloadJson | ConvertFrom-Json
        if (-not $snapshotData.ok) { throw "Snapshot rejected: $($snapshotData.error)" }
        if ($snapshotData.bindings.Count -lt 2) { throw "Expected 2 identities, got $($snapshotData.bindings.Count)." }
        Write-Host "    bindings=$($snapshotData.bindings.Count) known=$($snapshotData.knownNpcCount)" -ForegroundColor Green
        $target = $snapshotData.bindings[0]

        Write-Host "==> Conversation (offline or configured Gemini)"
        $reply = Invoke-Rpc $pipe "conversation.say" @{
            npcHandle = $target.handle; playerHandle = 1; playerText = "Hey, how is your day going?";
            conversationId = "smoke-test"; playerPosition = @{ x = 0; y = 0; z = 0 }; zone = "Vinewood"; weather = "Clear"; clockHour = 14
        }
        if ($reply.type -ne "conversation.say.result") { throw "Conversation failed: $($reply.payloadJson)" }
        $conversation = $reply.payloadJson | ConvertFrom-Json
        if (-not $conversation.ok) { throw "Conversation rejected: $($conversation.error)" }
        if ([string]::IsNullOrWhiteSpace($conversation.replyText)) { throw "NPC produced no dialogue." }
        Write-Host "    $($target.displayName) [$($conversation.voiceName)]: $($conversation.replyText)" -ForegroundColor Green
        Write-Host "    offlineFallback=$($conversation.usedOfflineFallback) latency=$($conversation.latencyMs)ms"

        Write-Host "SMOKE TEST PASSED" -ForegroundColor Green
    }
    finally {
        $pipe.Dispose()
    }
}
finally {
    Write-Host "==> Stopping service"
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
}
