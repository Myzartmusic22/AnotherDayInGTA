using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace AnotherDay.Plugin
{
    internal sealed class PluginConfig
    {
        public string DataDirectory { get; set; }
        public string PipeName { get; set; } = "AnotherDayInGTA";
        public bool AutoStartService { get; set; } = true;
        public string ServiceExePath { get; set; } = "";
        public string VoiceKey { get; set; } = "P";
        public string SocialKey { get; set; } = "Alt+Shift+7";
        public string StatusKey { get; set; } = "Alt+Shift+6";

        /// <summary>
        /// Ends the conversation. Separate from the talk key on purpose: letting
        /// go of push-to-talk must never hang up on the NPC mid-answer.
        /// </summary>
        public string HangupKey { get; set; } = "O";
        /// <summary>
        /// How close a pedestrian must be to own the conversation. This is the
        /// project's original default and also the service's identity radius; a
        /// narrower interaction bubble was tried and stopped conversations
        /// starting, because ambient pedestrians keep their distance.
        /// </summary>
        public int ConversationRangeMeters { get; set; } = 30;

        /// <summary>
        /// Seconds of total silence before an active conversation is allowed to
        /// end on its own. Long, because a thinking player, a pause to listen to
        /// the street, or a walk around the car is not the end of the talk.
        /// </summary>
        public int ConversationIdleTimeoutSeconds { get; set; } = 240;

        /// <summary>
        /// Part of the input device name, e.g. "Headset". Empty uses the Windows
        /// default recording device.
        /// </summary>
        public string MicDevice { get; set; } = "";

        /// <summary>
        /// Microphone level (0..1 RMS) that counts as the player talking over an
        /// NPC. Below it, background noise and the NPC's own voice from the
        /// speakers are not forwarded while audio is playing.
        /// </summary>
        public float BargeInThreshold { get; set; } = 0.02f;
        public int SnapshotIntervalMs { get; set; } = 1200;
        public int MaxNearbyPeds { get; set; } = 48;
        public float Volume { get; set; } = 1.0f;

        /// <summary>
        /// Places a pedestrian's voice at their own position: quieter with
        /// distance and panned to the side they are standing on. Off plays every
        /// line at the configured volume, as before.
        /// </summary>
        public bool SpatialAudio { get; set; } = true;

        /// <summary>Inside this radius the speaker is at full volume.</summary>
        public float AudioNearDistanceMeters { get; set; } = 3f;

        /// <summary>At this radius the speaker is down to a distant voice.</summary>
        public float AudioFarDistanceMeters { get; set; } = 45f;
        public bool ShowHud { get; set; } = true;
        public bool Debug { get; set; } = false;

        [JsonIgnore]
        public string ConfigPath { get; private set; }

        [JsonIgnore]
        public string ResolvedDataDirectory { get; private set; }

        [JsonIgnore]
        public Hotkey VoiceHotkey { get; private set; } = new Hotkey();

        [JsonIgnore]
        public Hotkey SocialHotkey { get; private set; } = new Hotkey();

        [JsonIgnore]
        public Hotkey StatusHotkey { get; private set; } = new Hotkey();

        [JsonIgnore]
        public Hotkey HangupHotkey { get; private set; } = new Hotkey();

        [JsonIgnore]
        public bool MigratedKeys { get; private set; }

        public static PluginConfig Load(string pluginDirectory)
        {
            string path = Path.Combine(pluginDirectory, "AnotherDay.Plugin.json");
            PluginConfig config = null;
            if (File.Exists(path))
            {
                try { config = JsonConvert.DeserializeObject<PluginConfig>(File.ReadAllText(path)); }
                catch (Exception ex) { PluginLog.Warn("Could not read plugin config, using defaults: " + ex.Message); }
            }
            config ??= new PluginConfig();
            config.ConfigPath = path;
            config.Normalize();
            if (!File.Exists(path) || config.MigratedKeys)
            {
                try { File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented), new UTF8Encoding(false)); }
                catch (Exception ex) { PluginLog.Warn("Could not write plugin config: " + ex.Message); }
            }
            return config;
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(PipeName)) PipeName = "AnotherDayInGTA";
            ConversationRangeMeters = Clamp(ConversationRangeMeters, 5, 80);
            ConversationIdleTimeoutSeconds = Clamp(ConversationIdleTimeoutSeconds, 30, 900);
            SnapshotIntervalMs = Clamp(SnapshotIntervalMs, 500, 5000);
            MaxNearbyPeds = Clamp(MaxNearbyPeds, 8, 96);
            Volume = Math.Min(1f, Math.Max(0f, Volume));
            AudioNearDistanceMeters = Math.Min(15f, Math.Max(0.5f, AudioNearDistanceMeters));
            AudioFarDistanceMeters = Math.Min(200f, Math.Max(AudioNearDistanceMeters + 1f, AudioFarDistanceMeters));
            BargeInThreshold = Math.Min(0.4f, Math.Max(0.002f, BargeInThreshold));

            string env = Environment.GetEnvironmentVariable("ANOTHERDAY_DATA_DIR");
            ResolvedDataDirectory = !string.IsNullOrWhiteSpace(DataDirectory)
                ? Path.GetFullPath(DataDirectory)
                : (!string.IsNullOrWhiteSpace(env)
                    ? Path.GetFullPath(env)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDayInGTA"));

            // Plain F9/F10 collide with GTA V's own bindings, so an untouched
            // legacy config is moved off them. Text chat no longer exists: every
            // conversation is spoken.
            string voiceKey = VoiceKey, socialKey = SocialKey, statusKey = StatusKey;
            MigratedKeys = Migrate(ref voiceKey, "F9", "P")
                | Migrate(ref socialKey, "F10", "Alt+Shift+7")
                | Migrate(ref statusKey, "F11", "Alt+Shift+6");
            VoiceKey = voiceKey;
            SocialKey = socialKey;
            StatusKey = statusKey;

            if (string.IsNullOrWhiteSpace(HangupKey)) HangupKey = "O";

            VoiceHotkey = Hotkey.Parse(VoiceKey) ?? new Hotkey(Keys.P);
            SocialHotkey = Hotkey.Parse(SocialKey) ?? new Hotkey(Keys.D7, alt: true, shift: true);
            StatusHotkey = Hotkey.Parse(StatusKey) ?? new Hotkey(Keys.D6, alt: true, shift: true);
            HangupHotkey = Hotkey.Parse(HangupKey) ?? new Hotkey(Keys.O);
        }

        private static bool Migrate(ref string value, string legacy, string replacement)
        {
            if (!string.Equals((value ?? "").Trim(), legacy, StringComparison.OrdinalIgnoreCase)) return false;
            value = replacement;
            return true;
        }

        private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

        public string TokenPath => Path.Combine(ResolvedDataDirectory, "ipc.token");

        public string ResolveServiceExe()
        {
            if (!string.IsNullOrWhiteSpace(ServiceExePath) && File.Exists(ServiceExePath)) return ServiceExePath;
            string local = Path.Combine(ResolvedDataDirectory, "service", "AnotherDay.Service.exe");
            if (File.Exists(local)) return local;
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AnotherDay.Service.exe");
            return File.Exists(beside) ? beside : null;
        }
    }

    /// <summary>
    /// A single keyboard binding with optional modifiers, written as
    /// "Alt+Shift+8" ("Alt+LSHIFT+8" also parses) or a plain "F8". Alt and Shift are held on both the press and the
    /// release so a chord cannot be mistaken for the game's own bindings.
    /// </summary>
    internal sealed class Hotkey
    {
        public Hotkey() { }

        public Hotkey(Keys key, bool alt = false, bool shift = false, bool control = false)
        {
            Key = key;
            Alt = alt;
            Shift = shift;
            Control = control;
        }

        public Keys Key { get; private set; } = Keys.None;
        public bool Alt { get; private set; }
        public bool Shift { get; private set; }
        public bool Control { get; private set; }
        public bool IsValid => Key != Keys.None;

        public static Hotkey Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var hotkey = new Hotkey();
            foreach (string raw in value.Split('+'))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                switch (token.ToLowerInvariant())
                {
                    case "alt":
                    case "lalt":
                    case "ralt": hotkey.Alt = true; continue;
                    case "shift":
                    case "lshift":
                    case "rshift": hotkey.Shift = true; continue;
                    case "ctrl":
                    case "control":
                    case "lctrl":
                    case "rctrl": hotkey.Control = true; continue;
                }

                Keys key = ParseKey(token);
                if (key != Keys.None) hotkey.Key = key;
            }
            return hotkey.IsValid ? hotkey : null;
        }

        private static Keys ParseKey(string token)
        {
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c >= '0' && c <= '9') return (Keys)((int)Keys.D0 + (c - '0'));
                if (c >= 'A' && c <= 'Z') return (Keys)((int)Keys.A + (c - 'A'));
            }
            return Enum.TryParse(token, true, out Keys parsed) ? parsed : Keys.None;
        }

        public bool Matches(KeyEventArgs e) =>
            e != null && e.KeyCode == Key && e.Alt == Alt && e.Shift == Shift && e.Control == Control;

        /// <summary>
        /// Key release ignores modifiers because the player normally lets go of
        /// Alt or Shift a fraction before the talk key.
        /// </summary>
        public bool MatchesKey(KeyEventArgs e) => e != null && e.KeyCode == Key;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(KeyLabel(Key));
            return string.Join("+", parts);
        }

        private static string KeyLabel(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + (key - Keys.NumPad0);
            return key.ToString();
        }
    }
}
