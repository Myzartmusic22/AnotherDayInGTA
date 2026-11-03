using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Ai;
using AnotherDay.Service.Data;
using AnotherDay.Service.Diagnostics;
using AnotherDay.Service.Domain;
using AnotherDay.Service.Live;
using AnotherDay.Service.Security;
using AnotherDay.Service.Simulation;
using AnotherDay.Shared;

namespace AnotherDay.Service
{
    /// <summary>
    /// Owns the long-lived service components and translates IPC requests into
    /// engine calls. All work here is asynchronous; nothing touches a game thread.
    /// </summary>
    public sealed class ServiceState
    {
        public AppConfig Config { get; private set; }
        public AppPaths Paths { get; }
        public Database Database { get; private set; }
        public NpcRegistry Registry { get; private set; }
        public Metrics Metrics { get; } = new Metrics();
        public SecretStore Secrets { get; }
        public GeminiClient GeminiClient { get; private set; }
        public GeminiProvider GeminiProvider { get; private set; }
        public OfflineProvider OfflineProvider { get; private set; }
        public ConversationEngine Conversation { get; private set; }
        public AudioService Audio { get; private set; }
        public VoiceEngine Voices { get; private set; }
        public LiveSessionManager Live { get; private set; }
        public SocialEngine Social { get; private set; }
        public WorldEventEngine WorldEvents { get; private set; }
        public string Token { get; private set; }
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public ServiceState(AppPaths paths, AppConfig config, string token)
        {
            Paths = paths;
            Config = config;
            Token = token;
            Secrets = new SecretStore(paths);
        }

        public async Task InitializeAsync()
        {
            Database = new Database(Paths);
            await Database.InitializeAsync().ConfigureAwait(false);

            var identity = new IdentityGenerator();
            var emotions = new EmotionEngine();
            var voices = new VoiceEngine(Database);
            Voices = voices;
            Registry = new NpcRegistry(Database, identity, emotions, voices, Config);

            GeminiClient = new GeminiClient(Config, Metrics, Secrets);
            GeminiProvider = new GeminiProvider(GeminiClient, Config);
            OfflineProvider = new OfflineProvider();

            var memory = new MemoryEngine(Database);
            Audio = new AudioService(Config, Paths, Database, GeminiProvider, Metrics);
            Conversation = new ConversationEngine(Config, Paths, Database, Registry, memory, emotions,
                Audio, new ActionRegistry(Registry), Metrics, GeminiProvider, OfflineProvider);
            Live = new LiveSessionManager(Config, GeminiClient, voices, Registry, memory, Database, Metrics);
            Social = new SocialEngine(Config, Database, Registry, Conversation, emotions, Metrics);
            WorldEvents = new WorldEventEngine(Config, Database, Registry, emotions, Metrics, Social);

            // Settings persisted from the last session win over config defaults.
            string saved = await Database.GetSettingAsync("userSettings").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                try
                {
                    SettingsDto settings = JsonSerializer.Deserialize<SettingsDto>(saved, JsonOptions);
                    if (settings != null) Config.UserSettings = settings;
                }
                catch (Exception ex) { Log.Warn("Saved user settings could not be read: " + ex.Message); }
            }
            Config.Normalize();
        }

        public async Task<MessageEnvelope> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
        {
            MessageEnvelope response = NewResponse(envelope);
            try
            {
                switch (envelope.Type)
                {
                    case MessageTypes.Hello:
                    {
                        var request = Parse<HandshakeRequest>(envelope.PayloadJson);
                        if (request == null || !ConstantTimeEquals(request.Token, Token))
                        {
                            response.Type = MessageTypes.Error;
                            response.PayloadJson = Serialize(new ErrorDto { Code = "unauthorized", Message = "Invalid IPC token." });
                            break;
                        }
                        if (request.ProtocolVersion != 0 && request.ProtocolVersion != Protocol.SchemaVersion)
                        {
                            response.Type = MessageTypes.Error;
                            response.PayloadJson = Serialize(new ErrorDto
                            {
                                Code = "protocol",
                                Message = "Protocol version mismatch. Expected " + Protocol.SchemaVersion + "."
                            });
                            break;
                        }
                        response.Type = MessageTypes.HelloResult;
                        response.PayloadJson = Serialize(new HandshakeResponse
                        {
                            Ok = true,
                            ServiceVersion = "0.1.0",
                            DataDirectory = Paths.Root,
                            AiConfigured = Conversation.AiConfigured,
                            LiveAvailable = Live != null && Live.Available,
                            LiveReason = Live == null ? "live voice is not initialised" : Live.UnavailableReason,
                            LiveModel = Config.Live.Model,
                            Settings = Config.UserSettings
                        });
                        Log.Info("Game connected (game " + (request.GameVersion ?? "unknown") + ", protocol " + request.ProtocolVersion + ")");
                        break;
                    }

                    case MessageTypes.Ping:
                        response.Type = MessageTypes.Pong;
                        response.PayloadJson = envelope.PayloadJson ?? "{}";
                        break;

                    case MessageTypes.PedSnapshot:
                    {
                        var request = Parse<PedSnapshotRequest>(envelope.PayloadJson) ?? new PedSnapshotRequest();
                        PedSnapshotResponse snapshot = await Registry.ProcessSnapshotAsync(request, ct).ConfigureAwait(false);
                        snapshot.Commands.AddRange(WorldEvents.DrainCommands());
                        snapshot.Speeches.AddRange(Social.DrainSpeeches());
                        snapshot.Commands = snapshot.Commands.Where(ActionRegistry.IsExecutable).Take(12).ToList();
                        snapshot.Speeches = snapshot.Speeches.Where(s =>
                            Registry.TryGet(s.NpcHandle, out ActiveNpc npc) &&
                            string.Equals(npc.Profile.NpcId, s.NpcId, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
                        snapshot.ActiveConversationCount = Social.ActiveConversationCount;
                        snapshot.AiAvailable = Conversation.AiConfigured && Config.UserSettings.AiEnabled;
                        snapshot.AiStatus = Conversation.AiConfigured
                            ? (Config.UserSettings.AiEnabled ? "online" : "paused in settings")
                            : (GeminiClient.AuthRejected ? "key rejected" : "no key");
                        // Every NPC line is Gemini native audio. There is no
                        // operating-system voice anywhere in the pipeline.
                        snapshot.VoiceAvailable = Config.Voice.Enabled && Config.UserSettings.VoicesEnabled &&
                            Conversation.AiConfigured;
                        snapshot.LiveAvailable = Live != null && Live.Available;
                        snapshot.LiveReason = Live == null ? "not initialised" : Live.UnavailableReason;
                        snapshot.LiveSessions = Live?.ActiveCount ?? 0;
                        snapshot.ServiceStatus = BuildStatus();
                        response.Type = MessageTypes.PedSnapshotResult;
                        response.PayloadJson = Serialize(snapshot);
                        break;
                    }

                    case MessageTypes.Converse:
                    {
                        var request = Parse<ConversationRequest>(envelope.PayloadJson) ?? new ConversationRequest();
                        ConversationResponse result = await Conversation.ConverseAsync(request, ct).ConfigureAwait(false);
                        response.Type = MessageTypes.ConverseResult;
                        response.PayloadJson = Serialize(result);
                        break;
                    }

                    case MessageTypes.VoiceConverse:
                    {
                        var request = Parse<VoiceConversationRequest>(envelope.PayloadJson) ?? new VoiceConversationRequest();
                        ConversationResponse result = await Conversation.VoiceConverseAsync(request, ct).ConfigureAwait(false);
                        response.Type = MessageTypes.VoiceConverseResult;
                        response.PayloadJson = Serialize(result);
                        break;
                    }

                    case MessageTypes.WorldEvents:
                    {
                        var request = Parse<WorldEventsRequest>(envelope.PayloadJson) ?? new WorldEventsRequest();
                        WorldEventsResponse result = await WorldEvents.IngestAsync(request, ct).ConfigureAwait(false);
                        response.Type = MessageTypes.WorldEventsResult;
                        response.PayloadJson = Serialize(result);
                        break;
                    }

                    case MessageTypes.TriggerSocial:
                    {
                        var request = Parse<TriggerSocialRequest>(envelope.PayloadJson) ?? new TriggerSocialRequest();
                        TriggerSocialResponse result = await Social.TriggerAsync(request, ct).ConfigureAwait(false);
                        response.Type = MessageTypes.TriggerSocialResult;
                        response.PayloadJson = Serialize(result);
                        break;
                    }

                    case MessageTypes.GetSettings:
                        response.Type = MessageTypes.SettingsResult;
                        response.PayloadJson = Serialize(new SettingsEnvelope { Ok = true, Settings = Config.UserSettings });
                        break;

                    case MessageTypes.UpdateSettings:
                    {
                        var envelopePayload = Parse<SettingsEnvelope>(envelope.PayloadJson);
                        if (envelopePayload?.Settings == null)
                        {
                            response.Type = MessageTypes.Error;
                            response.PayloadJson = Serialize(new ErrorDto { Code = "settings", Message = "No settings supplied." });
                            break;
                        }
                        Config.UserSettings = envelopePayload.Settings;
                        Config.Normalize();
                        Config.Save();
                        await Database.SaveSettingAsync("userSettings", JsonSerializer.Serialize(Config.UserSettings, JsonOptions)).ConfigureAwait(false);
                        response.Type = MessageTypes.UpdateSettingsResult;
                        response.PayloadJson = Serialize(new SettingsEnvelope { Ok = true, Settings = Config.UserSettings });
                        break;
                    }

                    case MessageTypes.GetStatus:
                        response.Type = MessageTypes.StatusResult;
                        response.PayloadJson = Serialize(await BuildStatusDtoAsync().ConfigureAwait(false));
                        break;

                    default:
                        response.Type = MessageTypes.Error;
                        response.PayloadJson = Serialize(new ErrorDto
                        {
                            Code = "unknown_type",
                            Message = "Unsupported message type: " + envelope.Type
                        });
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                response.Type = MessageTypes.Error;
                response.PayloadJson = Serialize(new ErrorDto { Code = "cancelled", Message = "The request was cancelled." });
            }
            catch (JsonException ex)
            {
                Log.Warn("Malformed IPC payload for " + envelope.Type + ": " + ex.Message);
                response.Type = MessageTypes.Error;
                response.PayloadJson = Serialize(new ErrorDto { Code = "payload", Message = "Malformed request payload." });
            }
            catch (Exception ex)
            {
                Log.Error("IPC request " + envelope.Type + " failed: " + ex.Message, ex);
                Metrics.RecordError(ex.Message);
                response.Type = MessageTypes.Error;
                response.PayloadJson = Serialize(new ErrorDto { Code = "internal", Message = ex.Message });
            }
            return response;
        }

        private async Task<StatusDto> BuildStatusDtoAsync()
        {
            (int Requests, long Tokens) usage = await Database.GetTodayUsageAsync().ConfigureAwait(false);
            List<(string Key, string Path, long Bytes, DateTime UsedUtc)> cache =
                await Database.GetAudioCacheEntriesAsync().ConfigureAwait(false);

            return new StatusDto
            {
                ServiceVersion = "0.1.0",
                AiConfigured = Conversation.AiConfigured,
                LiveAvailable = Live != null && Live.Available,
                AiLastCallSucceeded = Metrics.LastAiCallSucceeded,
                TextModel = Config.Gemini.TextModel,
                TtsModel = Config.Gemini.TtsModel,
                TranscribeModel = Config.Gemini.TranscribeModel,
                LiveModel = Config.Live.Model,
                TotalNpcs = await Database.CountProfilesAsync().ConfigureAwait(false),
                KnownNpcs = await Database.CountProfilesAsync().ConfigureAwait(false),
                ActiveNpcs = Registry.Active.Count,
                ActiveConversations = Social.ActiveConversationCount,
                ApiRequestsToday = usage.Requests > 0 ? usage.Requests : (int)Metrics.ApiRequestsToday,
                EstimatedTokensToday = usage.Tokens > 0 ? usage.Tokens : Metrics.EstimatedTokensToday,
                CachedAudioFiles = cache.Count,
                CachedAudioBytes = cache.Sum(c => c.Bytes),
                DatabasePath = Paths.DatabasePath,
                DataDirectory = Paths.Root,
                UptimeSeconds = (DateTime.UtcNow - StartedUtc).TotalSeconds,
                LastError = Metrics.LastError,
                PipelineStatus = BuildStatus(),
                Settings = Config.UserSettings
            };
        }

        private string BuildStatus()
        {
            var parts = new List<string>();
            parts.Add(Conversation.AiConfigured
                ? (Config.UserSettings.AiEnabled ? "Gemini ready" : "Gemini configured but paused")
                : (GeminiClient.AuthRejected
                    ? "Offline behavior (Gemini rejected the API key)"
                    : "Offline behavior (no API key)"));
            parts.Add(Live != null && Live.Available
                ? "Live voice ready"
                : "Live voice off (" + (Live?.UnavailableReason ?? "not initialised") + ")");
            parts.Add("DB " + (Database.IsAvailable ? "online" : "memory-only"));
            parts.Add(Registry.Active.Count + " NPCs active");
            return string.Join(" | ", parts);
        }

        public static T Parse<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

        private static MessageEnvelope NewResponse(MessageEnvelope request) => new MessageEnvelope
        {
            Type = MessageTypes.Error,
            CorrelationId = request?.CorrelationId ?? request?.MessageId
        };

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
