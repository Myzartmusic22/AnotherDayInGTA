using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Diagnostics;

namespace AnotherDay.Service.Live
{
    public sealed class LiveApiException : Exception
    {
        public LiveApiException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>One decoded BidiGenerateContentServerMessage.</summary>
    public sealed class LiveServerEvent
    {
        public bool SetupComplete { get; set; }
        public bool Interrupted { get; set; }
        public bool TurnComplete { get; set; }
        public bool GenerationComplete { get; set; }
        public bool GoAway { get; set; }
        public string GoAwayTime { get; set; }
        public string InputTranscript { get; set; }
        public string OutputTranscript { get; set; }
        public string ResumptionHandle { get; set; }
        public bool Resumable { get; set; }
        public string ErrorMessage { get; set; }
        public long TotalTokens { get; set; }
        public List<byte[]> Audio { get; } = new List<byte[]>();

        public bool HasAudio => Audio.Count > 0;
    }

    /// <summary>
    /// Gemini Live API session client (BidiGenerateContent over WebSocket).
    /// Native-audio only: the model returns 24 kHz PCM, so no text-to-speech of
    /// any kind sits between the model and the game.
    /// </summary>
    public sealed class GeminiLiveClient : IDisposable
    {
        public const int InputSampleRate = 16000;
        public const int OutputSampleRate = 24000;
        public const string InputMimeType = "audio/pcm;rate=16000";

        private readonly AppConfig _config;
        private readonly Func<ILiveTransport> _transportFactory;
        private ILiveTransport _transport;
        private readonly string _apiKey;
        private readonly string _voiceName;
        private readonly string _persona;
        private readonly string _model;
        private readonly string _resumptionHandle;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _loopCts;
        private Task _receiveLoop;
        private volatile bool _closed;
        private bool _flatSetup;
        private bool _setupComplete;

        public GeminiLiveClient(AppConfig config, Func<ILiveTransport> transportFactory, string apiKey,
            string model, string voiceName, string persona, string resumptionHandle = null)
        {
            _config = config;
            _transportFactory = transportFactory ?? (() => new WebSocketLiveTransport());
            _apiKey = apiKey;
            _model = NormalizeModel(model);
            _voiceName = string.IsNullOrWhiteSpace(voiceName) ? "Kore" : voiceName;
            _persona = persona ?? "";
            _resumptionHandle = resumptionHandle;
        }

        public string Model => _model;
        public string VoiceName => _voiceName;
        public string ResumptionHandle { get; private set; }
        public bool IsReady => _setupComplete && !_closed;
        public string CloseReason { get; private set; }

        public event Action<byte[]> AudioReceived;
        public event Action Interrupted;
        public event Action TurnComplete;
        public event Action<string> InputTranscript;
        public event Action<string> OutputTranscript;
        public event Action<string> GoAway;
        public event Action<string> Closed;
        public event Action<string> Warning;

        public Uri BuildUri(string apiBaseUrl, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new LiveApiException("Gemini API key is not configured.");
            string baseUrl = (apiBaseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
            if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "wss://" + baseUrl.Substring("https://".Length);
            else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "ws://" + baseUrl.Substring("http://".Length);

            return new Uri(baseUrl + "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" +
                Uri.EscapeDataString(key));
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) throw new LiveApiException("Gemini API key is not configured.");

            for (int attempt = 0; attempt < 2; attempt++)
            {
                _transport = _transportFactory();
                try
                {
                    await _transport.ConnectAsync(BuildUri(_config.Gemini.ApiBaseUrl, _apiKey), ct).ConfigureAwait(false);
                    await SendSetupAsync(ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt == 0 && !_flatSetup)
                {
                    // The raw WebSocket guide puts responseModalities at the setup
                    // root while the message schema nests it under generationConfig.
                    // Rather than guess per account, retry once with the flat form.
                    Warning?.Invoke("Live setup rejected, retrying with flat responseModalities: " + ex.Message);
                    _flatSetup = true;
                    try { _transport?.Dispose(); } catch { }
                    if (attempt == 1) throw;
                    if (ex is LiveApiException) { }
                }
                catch
                {
                    try { _transport?.Dispose(); } catch { }
                    throw;
                }
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        }

        private async Task SendSetupAsync(CancellationToken ct)
        {
            string setup = BuildSetupJson(_model, _voiceName, _persona, _config.Live, _resumptionHandle, _flatSetup);
            await _transport.SendAsync(setup, ct).ConfigureAwait(false);

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _config.Service.AiTimeoutSeconds)));
            while (!_setupComplete)
            {
                string raw;
                try
                {
                    raw = await _transport.ReceiveAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LiveApiException("Gemini Live did not answer the setup within " + _config.Service.AiTimeoutSeconds + "s.");
                }

                if (raw == null)
                    throw new LiveApiException("Gemini Live closed the connection during setup: " + (_transport.CloseReason ?? "no reason given"));

                LiveServerEvent evt = ParseServerMessage(raw);
                if (evt.ErrorMessage != null) throw new LiveApiException("Gemini Live: " + evt.ErrorMessage);
                if (evt.SetupComplete)
                {
                    _setupComplete = true;
                    Log.Info("Gemini Live session ready (model " + _model + ", voice " + _voiceName + ")");
                    break;
                }
                Dispatch(evt);
            }
        }

        public async Task SendAudioAsync(byte[] pcm16k, CancellationToken ct)
        {
            if (!IsReady || pcm16k == null || pcm16k.Length == 0) return;
            string json = BuildAudioMessage(pcm16k);
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_transport.IsOpen) return;
                await _transport.SendAsync(json, ct).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }

        /// <summary>Sends text as a completed user turn (used to seed prior conversation context).</summary>
        public async Task SendTextAsync(string text, CancellationToken ct)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(text)) return;
            string json = "{\"clientContent\":{\"turns\":[{\"role\":\"user\",\"parts\":[{\"text\":" +
                JsonSerializer.Serialize(text) + "}]}],\"turnComplete\":true}}";
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_transport.IsOpen) return;
                await _transport.SendAsync(json, ct).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }

        /// <summary>
        /// Push-to-talk release: the microphone stream for this turn is finished.
        /// Telling the server explicitly is what lets it answer the whole
        /// sentence instead of the fragment its silence detector happened to see.
        /// </summary>
        public async Task SendAudioStreamEndAsync(CancellationToken ct)
        {
            if (!IsReady) return;
            const string json = "{\"realtimeInput\":{\"audioStreamEnd\":true}}";
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_transport.IsOpen) return;
                await _transport.SendAsync(json, ct).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string raw = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                    if (raw == null) break;
                    Dispatch(ParseServerMessage(raw));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("Gemini Live receive loop ended: " + ex.Message);
            }
            finally
            {
                _closed = true;
                CloseReason = CloseReason ?? _transport.CloseReason ?? "connection closed";
                Closed?.Invoke(CloseReason);
            }
        }

        private void Dispatch(LiveServerEvent evt)
        {
            if (evt == null) return;
            if (evt.ResumptionHandle != null) ResumptionHandle = evt.ResumptionHandle;

            if (evt.HasAudio)
            {
                foreach (byte[] chunk in evt.Audio) AudioReceived?.Invoke(chunk);
            }
            if (evt.Interrupted) Interrupted?.Invoke();
            if (!string.IsNullOrWhiteSpace(evt.InputTranscript)) InputTranscript?.Invoke(evt.InputTranscript);
            if (!string.IsNullOrWhiteSpace(evt.OutputTranscript)) OutputTranscript?.Invoke(evt.OutputTranscript);
            if (evt.TurnComplete) TurnComplete?.Invoke();
            if (evt.GoAway)
            {
                CloseReason = "goAway: " + (evt.GoAwayTime ?? "session ending");
                GoAway?.Invoke(evt.GoAwayTime ?? "");
            }
            if (evt.ErrorMessage != null) Warning?.Invoke("Gemini Live: " + evt.ErrorMessage);
        }

        // -------------------------------------------------------------- wire format

        public static string BuildAudioMessage(byte[] pcm16k) =>
            "{\"realtimeInput\":{\"audio\":{\"data\":\"" + Convert.ToBase64String(pcm16k) +
            "\",\"mimeType\":\"" + InputMimeType + "\"}}}";

        public static string BuildSetupJson(string model, string voiceName, string persona,
            LiveConfig live, string resumptionHandle, bool flatModalities)
        {
            var speech = new Dictionary<string, object>
            {
                ["voiceConfig"] = new Dictionary<string, object>
                {
                    ["prebuiltVoiceConfig"] = new Dictionary<string, object> { ["voiceName"] = voiceName }
                }
            };

            var setup = new Dictionary<string, object>
            {
                ["model"] = NormalizeModel(model),
                ["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = persona ?? "" } }
                },
                ["realtimeInputConfig"] = new Dictionary<string, object>
                {
                    ["automaticActivityDetection"] = new Dictionary<string, object>
                    {
                        ["disabled"] = false,
                        ["startOfSpeechSensitivity"] = live?.StartSensitivity ?? "START_SENSITIVITY_HIGH",
                        ["endOfSpeechSensitivity"] = live?.EndSensitivity ?? "END_SENSITIVITY_LOW",
                        ["prefixPaddingMs"] = Math.Max(0, live?.PrefixPaddingMs ?? 20),
                        ["silenceDurationMs"] = Math.Max(100, live?.SilenceDurationMs ?? 500)
                    }
                },
                ["inputAudioTranscription"] = new Dictionary<string, object>(),
                ["outputAudioTranscription"] = new Dictionary<string, object>(),
                ["contextWindowCompression"] = new Dictionary<string, object>
                {
                    ["slidingWindow"] = new Dictionary<string, object>
                    {
                        ["targetTokens"] = Math.Max(4000, live?.ContextWindowTokens ?? 16000)
                    }
                },
                ["sessionResumption"] = resumptionHandle == null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object> { ["handle"] = resumptionHandle }
            };

            var generation = new Dictionary<string, object>
            {
                ["responseModalities"] = new object[] { "AUDIO" },
                ["speechConfig"] = speech
            };

            if (flatModalities)
            {
                setup["responseModalities"] = new object[] { "AUDIO" };
                setup["speechConfig"] = speech;
            }
            else
            {
                setup["generationConfig"] = generation;
            }

            return JsonSerializer.Serialize(new Dictionary<string, object> { ["setup"] = setup });
        }

        /// <summary>Decodes a BidiGenerateContentServerMessage into the flat shape the session consumes.</summary>
        public static LiveServerEvent ParseServerMessage(string json)
        {
            var evt = new LiveServerEvent();
            if (string.IsNullOrWhiteSpace(json)) return evt;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException)
            {
                evt.ErrorMessage = "unreadable server message";
                return evt;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return evt;

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                {
                    evt.ErrorMessage = ReadString(error, "message") ?? "unknown error";
                    string status = ReadString(error, "status");
                    if (!string.IsNullOrWhiteSpace(status)) evt.ErrorMessage = status + ": " + evt.ErrorMessage;
                }

                if (root.TryGetProperty("setupComplete", out _)) evt.SetupComplete = true;

                if (root.TryGetProperty("goAway", out JsonElement goAway) && goAway.ValueKind == JsonValueKind.Object)
                {
                    evt.GoAway = true;
                    evt.GoAwayTime = ReadString(goAway, "timeLeft");
                }

                if (root.TryGetProperty("sessionResumptionUpdate", out JsonElement resume) && resume.ValueKind == JsonValueKind.Object)
                {
                    evt.ResumptionHandle = ReadString(resume, "newHandle");
                    if (resume.TryGetProperty("resumable", out JsonElement resumable) && resumable.ValueKind == JsonValueKind.True)
                        evt.Resumable = true;
                }

                if (root.TryGetProperty("usageMetadata", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("totalTokenCount", out JsonElement total) && total.TryGetInt64(out long tokens))
                    evt.TotalTokens = tokens;

                if (!root.TryGetProperty("serverContent", out JsonElement content) || content.ValueKind != JsonValueKind.Object)
                    return evt;

                if (content.TryGetProperty("interrupted", out JsonElement interrupted) && interrupted.ValueKind == JsonValueKind.True)
                    evt.Interrupted = true;
                if (content.TryGetProperty("turnComplete", out JsonElement turn) && turn.ValueKind == JsonValueKind.True)
                    evt.TurnComplete = true;
                if (content.TryGetProperty("generationComplete", out JsonElement gen) && gen.ValueKind == JsonValueKind.True)
                    evt.GenerationComplete = true;

                evt.InputTranscript = ReadString(content, "inputTranscription");
                evt.OutputTranscript = ReadString(content, "outputTranscription");

                if (content.TryGetProperty("modelTurn", out JsonElement modelTurn) && modelTurn.ValueKind == JsonValueKind.Object &&
                    modelTurn.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;

                        if (part.TryGetProperty("inlineData", out JsonElement inline) && inline.ValueKind == JsonValueKind.Object)
                        {
                            string mime = ReadString(inline, "mimeType") ?? "";
                            string data = ReadString(inline, "data");
                            if (string.IsNullOrWhiteSpace(data)) continue;
                            if (mime.Length > 0 && mime.IndexOf("audio", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            try { evt.Audio.Add(Convert.FromBase64String(data)); }
                            catch (FormatException) { }
                        }

                        if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        {
                            // Native-audio models normally return only audio; a text
                            // part here is a model note, not something to speak.
                            string value = text.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) evt.OutputTranscript ??= value;
                        }
                    }
                }
            }
            return evt;
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(name, out JsonElement value)) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("text", out JsonElement text) &&
                text.ValueKind == JsonValueKind.String)
                return text.GetString();
            return null;
        }

        public static string NormalizeModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "models/gemini-3.8-live";
            return model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? model : "models/" + model;
        }

        public void Dispose()
        {
            _closed = true;
            try { _loopCts?.Cancel(); } catch { }
            try { _transport.Dispose(); } catch { }
            try { _loopCts?.Dispose(); } catch { }
        }
    }
}
