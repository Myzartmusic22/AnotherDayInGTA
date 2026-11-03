using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Diagnostics;
using AnotherDay.Shared;

namespace AnotherDay.Service.Live
{
    /// <summary>
    /// One NPC's live spoken conversation: a Gemini Live session with that NPC's
    /// voice and persona, kept open while the player is talking to them. Handles
    /// idle expiry, session resumption after a goAway/connection drop, and turn
    /// transcripts so the conversation can be remembered afterwards.
    /// </summary>
    public sealed class LiveSession : IDisposable
    {
        private readonly AppConfig _config;
        private readonly Func<string, CancellationToken, Task<GeminiLiveClient>> _connect;
        private readonly object _sync = new object();
        private readonly StringBuilder _playerTurn = new StringBuilder();
        private readonly StringBuilder _npcTurn = new StringBuilder();
        private GeminiLiveClient _client;
        private CancellationTokenSource _cts;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private DateTime _startedUtc = DateTime.UtcNow;
        private bool _stopping;
        private bool _disposed;
        private int _reconnects;
        private bool _farewellRaised;
        private Task _watchdog;

        public LiveSession(AppConfig config, Func<string, CancellationToken, Task<GeminiLiveClient>> connect,
            string npcId, string displayName, string voiceName, string model)
        {
            _config = config;
            _connect = connect;
            NpcId = npcId;
            DisplayName = displayName;
            VoiceName = voiceName;
            Model = model;
        }

        public string NpcId { get; }
        public string DisplayName { get; }
        public string VoiceName { get; }
        public string Model { get; }
        public string LastError { get; private set; }
        public int Reconnects => _reconnects;

        public bool IsConnected
        {
            get { lock (_sync) return _client != null && _client.IsReady; }
        }

        public double IdleSeconds
        {
            get { lock (_sync) return (DateTime.UtcNow - _lastActivityUtc).TotalSeconds; }
        }

        public double Seconds => (DateTime.UtcNow - _startedUtc).TotalSeconds;

        public event Action<byte[]> AudioOut;
        public event Action Interrupted;
        public event Action<string, string> Transcript;
        public event Action<string, string> TurnCommitted;
        public event Action<string> Closed;

        /// <summary>
        /// Raised once, after a completed player turn that was a goodbye. The
        /// session stays open so the NPC's short farewell can actually be heard;
        /// the plugin releases everything after that audio drains.
        /// </summary>
        public event Action Farewell;

        public async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _stopping = false;
            _client = await _connect(null, _cts.Token).ConfigureAwait(false);
            Wire(_client);
            _watchdog = Task.Run(() => WatchdogAsync(_cts.Token));
        }

        private void Wire(GeminiLiveClient client)
        {
            client.AudioReceived += chunk =>
            {
                lock (_sync) _lastActivityUtc = DateTime.UtcNow;
                AudioOut?.Invoke(chunk);
            };
            client.Interrupted += () => Interrupted?.Invoke();
            client.InputTranscript += text =>
            {
                lock (_sync) _playerTurn.Append(text);
                Transcript?.Invoke("player", text);
            };
            client.OutputTranscript += text =>
            {
                lock (_sync) _npcTurn.Append(text);
                Transcript?.Invoke("npc", text);
            };
            client.TurnComplete += CommitTurn;
            client.Warning += message => LastError = message;
            client.GoAway += timeLeft => { _ = ReconnectAsync("session limit reached"); };
            client.Closed += reason => { _ = ReconnectAsync(reason); };
        }

        private void CommitTurn()
        {
            string player;
            string npc;
            lock (_sync)
            {
                player = _playerTurn.ToString().Trim();
                npc = _npcTurn.ToString().Trim();
                _playerTurn.Clear();
                _npcTurn.Clear();
                _lastActivityUtc = DateTime.UtcNow;
            }
            if (player.Length == 0 && npc.Length == 0) return;
            TurnCommitted?.Invoke(player, npc);

            if (!_farewellRaised && FarewellDetector.IsFarewell(player))
            {
                _farewellRaised = true;
                Log.Info("Player said goodbye to " + DisplayName + "; letting the farewell play out.");
                Farewell?.Invoke();
            }
        }

        public async Task FeedAsync(byte[] pcm16k, CancellationToken ct)
        {
            GeminiLiveClient client;
            lock (_sync)
            {
                client = _client;
                _lastActivityUtc = DateTime.UtcNow;
            }
            if (client == null || !client.IsReady) return;
            await client.SendAudioAsync(pcm16k, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The player released push-to-talk. The turn is closed here rather than
        /// left to server-side silence detection, which would cut a sentence at
        /// its first pause.
        /// </summary>
        public async Task EndTurnAsync(CancellationToken ct)
        {
            GeminiLiveClient client;
            lock (_sync)
            {
                client = _client;
                _lastActivityUtc = DateTime.UtcNow;
            }
            if (client == null || !client.IsReady) return;
            await client.SendAudioStreamEndAsync(ct).ConfigureAwait(false);
        }

        private async Task ReconnectAsync(string reason)
        {
            if (_stopping || _disposed) return;

            string handle = null;
            lock (_sync)
            {
                if (_reconnects >= 3 || _stopping) return;
                _reconnects++;
                handle = _client?.ResumptionHandle;
            }

            // A goAway or a dropped socket ends the session; the resumption handle
            // carries the conversation across so the NPC does not forget the last
            // minute of talking.
            Log.Warn("Live session for " + DisplayName + " reconnecting (" + reason + "), attempt " + _reconnects);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * _reconnects), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                CancelClient();
                GeminiLiveClient client = await _connect(handle, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                lock (_sync) _client = client;
                Wire(client);
                Log.Info("Live session for " + DisplayName + " resumed");
            }
            catch (Exception ex)
            {
                LastError = "reconnect failed: " + ex.Message;
                Log.Warn("Live session reconnect failed for " + DisplayName + ": " + ex.Message);
                Closed?.Invoke("connection lost: " + ex.Message);
            }
        }

        private async Task WatchdogAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    if (IdleSeconds < _config.Live.IdleTimeoutSeconds) continue;
                    Log.Info("Live session for " + DisplayName + " idle for " + (int)IdleSeconds + "s, closing.");
                    await StopAsync("idle").ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task StopAsync(string reason)
        {
            if (_stopping) return;
            _stopping = true;
            try { _cts?.Cancel(); } catch { }
            CancelClient();
            try
            {
                if (_watchdog != null) await Task.WhenAny(_watchdog, Task.Delay(500)).ConfigureAwait(false);
            }
            catch { }
            Closed?.Invoke(reason ?? "stopped");
        }

        private void CancelClient()
        {
            GeminiLiveClient client;
            lock (_sync)
            {
                client = _client;
                _client = null;
            }
            if (client == null) return;
            try { client.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            try { _cts?.Cancel(); } catch { }
            CancelClient();
            try { _cts?.Dispose(); } catch { }
        }
    }
}
