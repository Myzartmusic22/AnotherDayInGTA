using System;
using System.Collections.Generic;

namespace AnotherDay.Shared
{
    public static class NpcCategories
    {
        public const string Civilian = "civilian";
        public const string Police = "police";
        public const string Gang = "gang";
        public const string Shopkeeper = "shopkeeper";
        public const string Business = "business";
        public const string Employee = "employee";
        public const string Driver = "driver";
        public const string Passenger = "passenger";
        public const string Security = "security";
        public const string Paramedic = "paramedic";
        public const string Firefighter = "firefighter";
        public const string Mechanic = "mechanic";
        public const string Homeless = "homeless";
        public const string Other = "other";
    }

    public static class ActionTypes
    {
        public const string Speak = "speak";
        public const string Face = "face";
        public const string Approach = "approach";
        public const string WalkAway = "walk_away";
        public const string Follow = "follow";
        public const string Flee = "flee";
        public const string CallForHelp = "call_for_help";
        public const string Surrender = "surrender";
        public const string Cooperate = "cooperate";
        public const string Refuse = "refuse";
        public const string ReactDanger = "react_danger";
        public const string InitiateConversation = "initiate_conversation";
        public const string LeaveVehicle = "leave_vehicle";
        public const string Stop = "stop";

        public static readonly string[] All =
        {
            Speak, Face, Approach, WalkAway, Follow, Flee, CallForHelp,
            Surrender, Cooperate, Refuse, ReactDanger, InitiateConversation,
            LeaveVehicle, Stop
        };

        public static bool IsValid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string a in All)
                if (string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static class WorldEventTypes
    {
        public const string Gunshot = "gunshot";
        public const string Explosion = "explosion";
        public const string Crash = "vehicle_crash";
        public const string PoliceActivity = "police_activity";
        public const string Fight = "fight";
        public const string Injury = "injury";
        public const string Threat = "threat";
        public const string Robbery = "robbery";
        public const string EmergencyVehicle = "emergency_vehicle";
        public const string WeatherChange = "weather_change";
        public const string ZoneChange = "zone_change";
        public const string Death = "death";
        public const string PlayerAiming = "player_aiming";
        public const string Other = "other";

        public static readonly string[] All =
        {
            Gunshot, Explosion, Crash, PoliceActivity, Fight, Injury, Threat, Robbery,
            EmergencyVehicle, WeatherChange, ZoneChange, Death, PlayerAiming, Other
        };

        public static bool IsValid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string t in All)
                if (string.Equals(t, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public sealed class Vec3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vec3() { }
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public double DistanceTo(Vec3 other)
        {
            if (other == null) return double.MaxValue;
            double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", X, Y, Z);
        }
    }

    public sealed class PedObservation
    {
        public int Handle { get; set; }
        public string Fingerprint { get; set; }
        public int ModelHash { get; set; }
        public string ModelName { get; set; }
        public bool IsMale { get; set; }
        public string CategoryHint { get; set; }
        public Vec3 Position { get; set; }
        public float Heading { get; set; }
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public int Armor { get; set; }
        public bool IsDead { get; set; }
        public bool IsInVehicle { get; set; }
        public int VehicleHandle { get; set; }
        public string VehicleModel { get; set; }
        public int VehicleSeat { get; set; }
        public int WeaponHash { get; set; }
        public bool IsArmed { get; set; }
        public bool IsInCombat { get; set; }
        public bool IsAiming { get; set; }
        public bool IsFleeing { get; set; }
        public float Speed { get; set; }
        public string RelationshipGroup { get; set; }
        public bool IsOnScreen { get; set; }
        public float DistanceToPlayer { get; set; }
        public int ArmorMax { get; set; }
    }

    public sealed class PedSnapshotRequest
    {
        public Vec3 PlayerPosition { get; set; }
        public float PlayerHeading { get; set; }
        public int PlayerHandle { get; set; }
        public bool PlayerArmed { get; set; }
        public bool PlayerAiming { get; set; }
        public int PlayerWantedLevel { get; set; }
        public string PlayerVehicleModel { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
        public int ClockMinute { get; set; }
        public List<PedObservation> Peds { get; set; } = new List<PedObservation>();
        public List<WorldEventObservation> Events { get; set; } = new List<WorldEventObservation>();
    }

    public sealed class WorldEventObservation
    {
        public string Type { get; set; }
        public Vec3 Position { get; set; }
        public float Radius { get; set; }
        public float Magnitude { get; set; }
        public int SourceHandle { get; set; }
        public int TargetHandle { get; set; }
        public string Description { get; set; }
        public string Zone { get; set; }
        public string EmittedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    }

    public sealed class PedBinding
    {
        public int Handle { get; set; }
        public string NpcId { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public string Occupation { get; set; }
        public bool IsNew { get; set; }
        public double Confidence { get; set; }
        public string VoiceName { get; set; }
        public string EmotionSummary { get; set; }
        public bool IsKnown { get; set; }
    }

    public sealed class PedSnapshotResponse
    {
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
        public List<PedBinding> Bindings { get; set; } = new List<PedBinding>();
        public List<ActionCommandDto> Commands { get; set; } = new List<ActionCommandDto>();
        public List<NpcSpeechEvent> Speeches { get; set; } = new List<NpcSpeechEvent>();
        public int KnownNpcCount { get; set; }
        public int ActiveConversationCount { get; set; }
        public bool AiAvailable { get; set; }
        /// <summary>Short reason shown next to "AI: offline" in the HUD, e.g. "key rejected".</summary>
        public string AiStatus { get; set; }
        public bool VoiceAvailable { get; set; }
        /// <summary>True when Gemini Live sessions can be opened for spoken conversations.</summary>
        public bool LiveAvailable { get; set; }
        public string LiveReason { get; set; }
        public int LiveSessions { get; set; }
        public string ServiceStatus { get; set; }
    }

    public sealed class ConversationRequest
    {
        public int NpcHandle { get; set; }
        public int PlayerHandle { get; set; }
        public string PlayerText { get; set; }
        public string ConversationId { get; set; }
        public List<NearbyPerson> Nearby { get; set; } = new List<NearbyPerson>();
        public Vec3 PlayerPosition { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
        public bool PlayerArmed { get; set; }
        public bool PlayerAiming { get; set; }
    }

    public sealed class NearbyPerson
    {
        public int Handle { get; set; }
        public string NpcId { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public float DistanceToPlayer { get; set; }
    }

    public sealed class VoiceConversationRequest
    {
        public int NpcHandle { get; set; }
        public int PlayerHandle { get; set; }
        public string AudioPath { get; set; }
        public string ConversationId { get; set; }
        public List<NearbyPerson> Nearby { get; set; } = new List<NearbyPerson>();
        public Vec3 PlayerPosition { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
        public bool PlayerArmed { get; set; }
    }

    public sealed class ConversationResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string ConversationId { get; set; }
        public string NpcId { get; set; }
        public string DisplayName { get; set; }
        public string Transcript { get; set; }
        public string ReplyText { get; set; }
        public string InnerThought { get; set; }
        public string VoiceName { get; set; }
        public string AudioPath { get; set; }
        public bool AudioFromFallbackVoice { get; set; }
        public string EmotionSummary { get; set; }
        public string RelationshipSummary { get; set; }
        public bool WantsToEnd { get; set; }
        public List<ActionCommandDto> Actions { get; set; } = new List<ActionCommandDto>();
        public List<string> MemoriesUsed { get; set; } = new List<string>();
        public long LatencyMs { get; set; }
        public bool UsedOfflineFallback { get; set; }
    }

    public sealed class ActionCommandDto
    {
        public string NpcId { get; set; }
        public int NpcHandle { get; set; }
        public string Action { get; set; }
        public int? TargetHandle { get; set; }
        public string Reason { get; set; }
        public int Priority { get; set; } = 3;
        public int DurationMs { get; set; } = 4000;
        public Vec3 Position { get; set; }
    }

    public sealed class NpcSpeechEvent
    {
        public string ConversationId { get; set; }
        public string NpcId { get; set; }
        public int NpcHandle { get; set; }
        public string DisplayName { get; set; }
        public string Text { get; set; }
        public string VoiceName { get; set; }
        public string AudioPath { get; set; }
        public bool AudioFromFallbackVoice { get; set; }
        public List<int> TargetHandles { get; set; } = new List<int>();
        public int DelayMs { get; set; }
        public bool IsAutonomous { get; set; }
        public List<ActionCommandDto> Actions { get; set; } = new List<ActionCommandDto>();
    }

    public sealed class WorldEventsRequest
    {
        public List<WorldEventObservation> Events { get; set; } = new List<WorldEventObservation>();
        public Vec3 PlayerPosition { get; set; }
        public string PlayerZone { get; set; }
        public List<PedObservation> NearbyPeds { get; set; } = new List<PedObservation>();
    }

    public sealed class WorldEventsResponse
    {
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
        public int Accepted { get; set; }
        public List<ActionCommandDto> Commands { get; set; } = new List<ActionCommandDto>();
    }

    public sealed class TriggerSocialRequest
    {
        public int NpcHandle { get; set; }
        public int OtherHandle { get; set; }
        public bool Force { get; set; }
    }

    public sealed class TriggerSocialResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string ConversationId { get; set; }
        public int StartedConversations { get; set; }
    }

    public sealed class SettingsDto
    {
        public bool MasterEnabled { get; set; } = true;
        public bool AutonomousEnabled { get; set; } = true;
        public bool VoicesEnabled { get; set; } = true;
        public bool HudEnabled { get; set; } = true;
        public bool AiEnabled { get; set; } = true;
        public int ConversationRangeMeters { get; set; } = 30;
        public int SimulationDensity { get; set; } = 32;
        public int BackgroundChatterPerMinute { get; set; } = 2;
        public int MemoryTopK { get; set; } = 8;
        public float Volume { get; set; } = 1.0f;
        public string PushToTalkKey { get; set; } = "P";
        public string InteractKey { get; set; } = "Alt+Shift+7";
        public string SettingsKey { get; set; } = "Alt+Shift+6";
        public int MaxActiveConversations { get; set; } = 2;
    }

    public sealed class SettingsEnvelope
    {
        public SettingsDto Settings { get; set; }
        public bool Ok { get; set; } = true;
        public string Error { get; set; }
    }

    public sealed class StatusDto
    {
        public string ServiceVersion { get; set; }
        public bool AiConfigured { get; set; }
        public bool AiLastCallSucceeded { get; set; }
        public string TextModel { get; set; }
        public string TtsModel { get; set; }
        public string TranscribeModel { get; set; }
        public bool LiveAvailable { get; set; }
        public string LiveModel { get; set; }
        public int TotalNpcs { get; set; }
        public int KnownNpcs { get; set; }
        public int ActiveNpcs { get; set; }
        public int ActiveConversations { get; set; }
        public int ApiRequestsToday { get; set; }
        public long EstimatedTokensToday { get; set; }
        public int CachedAudioFiles { get; set; }
        public long CachedAudioBytes { get; set; }
        public string DatabasePath { get; set; }
        public string DataDirectory { get; set; }
        public double UptimeSeconds { get; set; }
        public string LastError { get; set; }
        public string PipelineStatus { get; set; }
        public SettingsDto Settings { get; set; }
    }

    public sealed class HandshakeRequest
    {
        public string Token { get; set; }
        public string ClientVersion { get; set; }
        public string GameVersion { get; set; }
        public int ProtocolVersion { get; set; }
    }

    public sealed class HandshakeResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string ServiceVersion { get; set; }
        public string DataDirectory { get; set; }
        public bool AiConfigured { get; set; }
        public bool LiveAvailable { get; set; }
        public string LiveReason { get; set; }
        public string LiveModel { get; set; }
        public SettingsDto Settings { get; set; }
    }

    public sealed class ErrorDto
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public sealed class PingDto
    {
        public string SentUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public string Echo { get; set; }
    }
}
