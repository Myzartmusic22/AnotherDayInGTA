using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnotherDay.Shared;

namespace AnotherDay.Service
{
    public sealed class AppConfig
    {
        public ServiceConfig Service { get; set; } = new ServiceConfig();
        public GeminiConfig Gemini { get; set; } = new GeminiConfig();
        public SimulationConfig Simulation { get; set; } = new SimulationConfig();
        public VoiceConfig Voice { get; set; } = new VoiceConfig();
        public LiveConfig Live { get; set; } = new LiveConfig();
        public SettingsDto UserSettings { get; set; } = new SettingsDto();

        [JsonIgnore]
        public string SourcePath { get; set; }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static AppConfig Load(string path)
        {
            AppConfig config = null;
            if (File.Exists(path))
            {
                try
                {
                    config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
                }
                catch (Exception ex)
                {
                    // A corrupt config must never prevent the service from starting.
                    string quarantine = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    try { File.Copy(path, quarantine, true); } catch { }
                    Console.Error.WriteLine("[config] Invalid config, using defaults: " + ex.Message);
                }
            }

            config ??= new AppConfig();
            config.SourcePath = path;
            config.Normalize();
            return config;
        }

        public void Normalize()
        {
            Service ??= new ServiceConfig();
            Gemini ??= new GeminiConfig();
            Simulation ??= new SimulationConfig();
            Voice ??= new VoiceConfig();
            Live ??= new LiveConfig();
            UserSettings ??= new SettingsDto();

            if (string.IsNullOrWhiteSpace(Service.PipeName)) Service.PipeName = Protocol.DefaultPipeName;
            Service.MaxConcurrentAiRequests = Clamp(Service.MaxConcurrentAiRequests, 1, 8);
            Service.AiTimeoutSeconds = Clamp(Service.AiTimeoutSeconds, 5, 180);
            Service.RequestsPerMinute = Clamp(Service.RequestsPerMinute, 1, 240);
            Service.DailyRequestBudget = Clamp(Service.DailyRequestBudget, 1, 100000);

            if (string.IsNullOrWhiteSpace(Gemini.ApiBaseUrl)) Gemini.ApiBaseUrl = "https://generativelanguage.googleapis.com";
            Gemini.ApiBaseUrl = Gemini.ApiBaseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(Gemini.TextModel)) Gemini.TextModel = "gemini-3.8-flash";
            if (string.IsNullOrWhiteSpace(Gemini.TtsModel)) Gemini.TtsModel = "gemini-3.8-flash-tts";
            if (string.IsNullOrWhiteSpace(Gemini.TranscribeModel)) Gemini.TranscribeModel = "gemini-3.5-transcribe";

            Simulation.MaxPedsPerSnapshot = Clamp(Simulation.MaxPedsPerSnapshot, 4, 128);
            Simulation.MaxActiveConversations = Clamp(Simulation.MaxActiveConversations, 0, 8);
            Simulation.MemoryTopK = Clamp(Simulation.MemoryTopK, 2, 24);
            Simulation.MemoryMaxPerNpc = Clamp(Simulation.MemoryMaxPerNpc, 50, 5000);
            Simulation.BackgroundChatterPerMinute = Clamp(Simulation.BackgroundChatterPerMinute, 0, 30);
            Simulation.SnapshotIntervalMs = Clamp(Simulation.SnapshotIntervalMs, 250, 5000);

            Voice.MaxCacheMegabytes = Clamp(Voice.MaxCacheMegabytes, 16, 4096);
            Voice.Volume = Clamp(Voice.Volume, 0f, 1f);

            if (string.IsNullOrWhiteSpace(Live.Model)) Live.Model = "gemini-3.8-live";
            Live.MaxConcurrentSessions = Clamp(Live.MaxConcurrentSessions, 1, 4);
            Live.IdleTimeoutSeconds = Clamp(Live.IdleTimeoutSeconds, 30, 900);
            Live.SilenceDurationMs = Clamp(Live.SilenceDurationMs, 200, 2000);
            Live.PrefixPaddingMs = Clamp(Live.PrefixPaddingMs, 0, 500);
            Live.ContextWindowTokens = Clamp(Live.ContextWindowTokens, 4000, 100000);

            UserSettings.ConversationRangeMeters = Clamp(UserSettings.ConversationRangeMeters, 5, 120);
            UserSettings.SimulationDensity = Clamp(UserSettings.SimulationDensity, 4, 128);
            UserSettings.BackgroundChatterPerMinute = Clamp(UserSettings.BackgroundChatterPerMinute, 0, 30);
            UserSettings.MemoryTopK = Clamp(UserSettings.MemoryTopK, 2, 24);
            UserSettings.MaxActiveConversations = Clamp(UserSettings.MaxActiveConversations, 0, 8);
            UserSettings.Volume = Clamp(UserSettings.Volume, 0f, 1f);
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SourcePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = SourcePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, SourcePath, true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[config] Could not save config: " + ex.Message);
            }
        }

        private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
        private static float Clamp(float value, float min, float max) => Math.Min(max, Math.Max(min, value));
    }

    public sealed class ServiceConfig
    {
        public string PipeName { get; set; } = Protocol.DefaultPipeName;
        public int MaxConcurrentAiRequests { get; set; } = 2;
        public int AiTimeoutSeconds { get; set; } = 60;
        public int RequestsPerMinute { get; set; } = 30;
        public int DailyRequestBudget { get; set; } = 600;
        public bool AutoStart { get; set; } = true;
    }

    public sealed class GeminiConfig
    {
        public string ApiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
        public string TextModel { get; set; } = "gemini-3.8-flash";
        public string TtsModel { get; set; } = "gemini-3.8-flash-tts";
        public string TranscribeModel { get; set; } = "gemini-3.5-transcribe";
        public float Temperature { get; set; } = 0.9f;
        public int MaxOutputTokens { get; set; } = 700;
        public bool StoreInteractions { get; set; } = false;
    }

    public sealed class SimulationConfig
    {
        public int MaxPedsPerSnapshot { get; set; } = 48;
        public int MaxActiveConversations { get; set; } = 2;
        public int BackgroundChatterPerMinute { get; set; } = 2;
        public int SnapshotIntervalMs { get; set; } = 1000;
        public int MemoryTopK { get; set; } = 8;
        public int MemoryMaxPerNpc { get; set; } = 400;
        public int SocialPairRadiusMeters { get; set; } = 8;
        public int WorldReactionRadiusMeters { get; set; } = 35;
        public bool UseAiForNpcChatter { get; set; } = true;
        public bool AllowThirdPartyJoin { get; set; } = true;
        public double EmotionDecayPerHour { get; set; } = 0.12;
    }

    public sealed class VoiceConfig
    {
        public bool Enabled { get; set; } = true;
        public int MaxCacheMegabytes { get; set; } = 256;
        public float Volume { get; set; } = 1.0f;
    }

    /// <summary>
    /// Gemini Live (BidiGenerateContent) settings for real-time spoken
    /// conversations. Native audio only: the model streams 24 kHz PCM straight
    /// into the game, so nothing is ever read aloud by the operating system.
    /// </summary>
    public sealed class LiveConfig
    {
        public bool Enabled { get; set; } = true;
        public string Model { get; set; } = "gemini-3.8-live";

        /// <summary>
        /// Live allows one voice per session, so each speaking NPC needs its own
        /// session. Two lets the player talk to someone while a nearby pair holds
        /// their own audible conversation, and caps the audio bill.
        /// </summary>
        public int MaxConcurrentSessions { get; set; } = 2;
        /// <summary>
        /// Silence tolerated in one Live session before the service closes it.
        /// Generous by default: the plugin owns the conversation lifetime and
        /// this is only the safety net for an abandoned session.
        /// </summary>
        public int IdleTimeoutSeconds { get; set; } = 240;

        // Server-side voice activity detection.
        /// <summary>
        /// Silence before the server treats the player's turn as finished. Push-
        /// to-talk sends an explicit end-of-turn, so this only has to survive a
        /// pause for breath mid-sentence rather than detect the end of speech.
        /// </summary>
        public int SilenceDurationMs { get; set; } = 900;
        public int PrefixPaddingMs { get; set; } = 20;
        public string StartSensitivity { get; set; } = "START_SENSITIVITY_HIGH";
        public string EndSensitivity { get; set; } = "END_SENSITIVITY_LOW";

        public int ContextWindowTokens { get; set; } = 16000;
        public bool EnableTranscription { get; set; } = true;
    }
}
