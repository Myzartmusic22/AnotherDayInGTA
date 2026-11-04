using System;

namespace AnotherDay.Shared
{
    /// <summary>
    /// Frames on the dedicated voice pipe. The JSON request/response pipe is
    /// strictly one-in-flight, so streaming microphone audio at 10 frames per
    /// second would stall snapshots behind it; the live conversation therefore
    /// uses its own duplex pipe with the same frame codec but a stream-shaped
    /// message set.
    /// </summary>
    public static class VoiceMessageTypes
    {
        public const string Hello = "voice.hello";
        public const string HelloResult = "voice.hello.result";
        public const string Start = "voice.start";
        public const string Ready = "voice.ready";
        public const string AudioIn = "voice.audio.in";
        public const string AudioOut = "voice.audio.out";

        /// <summary>
        /// Push-to-talk release: the player finished their sentence, so the
        /// service closes the turn and lets the NPC answer all of it.
        /// </summary>
        public const string TurnEnd = "voice.turn.end";
        public const string Event = "voice.event";
        public const string Stop = "voice.stop";
        public const string Error = "voice.error";
    }

    public static class VoiceEventKinds
    {
        /// <summary>The model was interrupted (the player barged in). Flush playback.</summary>
        public const string Interrupted = "interrupted";
        public const string TurnComplete = "turnComplete";
        public const string InputTranscript = "inputTranscript";
        public const string OutputTranscript = "outputTranscript";
        public const string Closed = "closed";
        public const string Error = "error";
        public const string SessionEnded = "sessionEnded";

        /// <summary>
        /// The service recognised a spoken farewell in either side's completed
        /// turn. The plugin waits for the response audio to finish before
        /// releasing the conversation.
        /// </summary>
        public const string Farewell = "farewell";
    }

    public sealed class VoiceHelloRequest
    {
        public string Token { get; set; }
        public string ClientVersion { get; set; }
        public int ProtocolVersion { get; set; }
    }

    public sealed class VoiceHelloResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Model { get; set; }
        public bool LiveAvailable { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Everything the service needs to open a Gemini Live session for one NPC.</summary>
    public sealed class VoiceStartRequest
    {
        public int NpcHandle { get; set; }
        public string NpcId { get; set; }
        public int PlayerHandle { get; set; }
        public string ConversationId { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
        public bool PlayerArmed { get; set; }
        public bool PlayerAiming { get; set; }

        /// <summary>
        /// Vehicle context at the moment the player spoke. The NPC keeps this
        /// context for the whole conversation: a driver stays at the wheel and
        /// answers through the window instead of stepping out.
        /// </summary>
        public bool NpcInVehicle { get; set; }
        public bool NpcIsDriver { get; set; }
        public string NpcVehicleModel { get; set; }
        public bool NpcOnRoad { get; set; }
        public bool NpcVehiclesNearby { get; set; }

        /// <summary>
        /// Sex of the actual model the player is looking at. The service validates
        /// the assigned voice against this rather than trusting a name or a stored
        /// profile that may have been created from a misclassified ped.
        /// </summary>
        public bool? NpcIsMale { get; set; }
    }

    public sealed class VoiceReadyResponse
    {
        /// <summary>Echo of the conversation this session was opened for, so a
        /// late answer from a previous partner is recognisable.</summary>
        public string ConversationId { get; set; }
        public string NpcId { get; set; }
        public string DisplayName { get; set; }
        public string VoiceName { get; set; }
        public string Model { get; set; }
        public bool Resumed { get; set; }

        /// <summary>
        /// Personality and mood for the animation controller, so gestures and
        /// facial expressions match the person the player is actually hearing.
        /// </summary>
        public AnimationStyleDto Animation { get; set; }
    }

    /// <summary>Microphone audio, base64 16-bit little-endian mono PCM at 16 kHz.</summary>
    public sealed class VoiceAudioChunk
    {
        public string Data { get; set; }
        public int Sequence { get; set; }

        public byte[] ToBytes()
        {
            if (string.IsNullOrEmpty(Data)) return Array.Empty<byte>();
            try { return Convert.FromBase64String(Data); }
            catch (FormatException) { return Array.Empty<byte>(); }
        }

        public static VoiceAudioChunk From(byte[] pcm, int sequence) => new VoiceAudioChunk
        {
            Data = Convert.ToBase64String(pcm ?? Array.Empty<byte>()),
            Sequence = sequence
        };
    }

    public sealed class VoiceEventMessage
    {
        public string Kind { get; set; }
        public string Text { get; set; }
        public int Sequence { get; set; }
    }

    /// <summary>
    /// Session telemetry the service reports back so the HUD can show what is
    /// actually happening instead of guessing.
    /// </summary>
    public sealed class VoiceSessionStats
    {
        public string NpcId { get; set; }
        public string VoiceName { get; set; }
        public long AudioInBytes { get; set; }
        public long AudioOutBytes { get; set; }
        public int Turns { get; set; }
        public double Seconds { get; set; }
    }
}
