using System;

namespace AnotherDay.Shared
{
    /// <summary>
    /// Where an active conversation is in its life cycle. Connecting covers the
    /// socket handshake, Active is a conversation the player can keep talking
    /// in, and Ended is terminal for that engagement.
    /// </summary>
    public enum EngagementPhase
    {
        Idle,
        Connecting,
        Active,
        Ended
    }

    /// <summary>Why an engagement stopped. Kept explicit so the HUD, logs and
    /// tests can tell a player hang-up from an interrupted one.</summary>
    public enum ConversationEndReason
    {
        None,
        PlayerStopped,
        OutOfRange,
        Unavailable,
        NpcDied,
        PlayerDied,
        Emergency,
        Inactivity,
        SessionLost,
        PluginUnload,

        /// <summary>The player got into a vehicle mid-conversation. Ending it is
        /// what keeps a pedestrian task off a driving player.</summary>
        PlayerEnteredVehicle,

        /// <summary>The player pressed P beside somebody else. The old partner is
        /// released and the new one is addressed, never both at once.</summary>
        PlayerSwitchedTarget
,
        /// <summary>The player said goodbye out loud. The NPC gets a short
        /// farewell, then the conversation is released normally.</summary>
        PlayerSaidGoodbye
    }

    /// <summary>What the plugin should do to the NPC right now to hold it.</summary>
    public enum EngagementHold
    {
        None,
        FacePlayer,
        StayInVehicle
    }

    public sealed class EngagementSettings
    {
        /// <summary>How often an on-foot NPC is turned back toward the player.</summary>
        public double FaceIntervalSeconds { get; set; } = 1.5;

        /// <summary>How often a driver is asked to stay put at the wheel.</summary>
        public double HoldIntervalSeconds { get; set; } = 1.0;

        /// <summary>Beyond this the player has genuinely walked off.</summary>
        public double LeaveRadiusMeters { get; set; } = 45.0;

        /// <summary>
        /// Silence is not the end of a conversation. A person thinking, reading
        /// the street, or walking around the car can go a long time without
        /// speaking and still be talking to you.
        /// </summary>
        public double InactivityTimeoutSeconds { get; set; } = 240.0;
    }

    public sealed class EngagementDecision
    {
        public bool Ends { get; set; }
        public ConversationEndReason Reason { get; set; }
        public EngagementHold Hold { get; set; }

        public static EngagementDecision Continue(EngagementHold hold)
        {
            return new EngagementDecision { Ends = false, Reason = ConversationEndReason.None, Hold = hold };
        }

        public static EngagementDecision End(ConversationEndReason reason)
        {
            return new EngagementDecision { Ends = true, Reason = reason, Hold = EngagementHold.None };
        }
    }

    /// <summary>
    /// The state of one player-to-NPC conversation: who the partner is, whether
    /// they are on foot or driving, when each side last spoke, and whether the
    /// interaction is still alive. This is deliberately free of game code so the
    /// rules that decide "does this NPC stay?" are testable without GTA V.
    ///
    /// A conversation never ends because the model finished a response or
    /// because the player paused; only an explicit stop, real distance, death,
    /// an emergency, a lost session or the configured inactivity window ends it.
    /// </summary>
    public sealed class EngagementState
    {
        private readonly EngagementSettings _settings;

        public EngagementState(EngagementSettings settings)
        {
            _settings = settings ?? new EngagementSettings();
        }

        public EngagementSettings Settings { get { return _settings; } }

        public int NpcHandle { get; private set; }
        public string NpcId { get; private set; }
        public EngagementPhase Phase { get; private set; }
        public DateTime StartedUtc { get; private set; }
        public DateTime LastPlayerSpeechUtc { get; private set; }
        public DateTime LastNpcResponseUtc { get; private set; }
        public DateTime LastActivityUtc { get; private set; }
        public DateTime NextHoldUtc { get; private set; }
        public DateTime EndedUtc { get; private set; }
        public ConversationEndReason EndReason { get; private set; }
        public int PlayerTurns { get; private set; }
        public int NpcTurns { get; private set; }

        // Vehicle context is captured at the start and preserved for the whole
        // conversation: the plugin must never issue an enter or leave task.
        public bool InVehicle { get; private set; }
        public bool IsDriver { get; private set; }
        public int VehicleHandle { get; private set; }

        public bool IsActive { get { return Phase == EngagementPhase.Active; } }

        public void Begin(DateTime nowUtc, int npcHandle, string npcId, bool inVehicle, bool isDriver, int vehicleHandle)
        {
            NpcHandle = npcHandle;
            NpcId = npcId;
            Phase = EngagementPhase.Connecting;
            StartedUtc = nowUtc;
            LastActivityUtc = nowUtc;
            LastPlayerSpeechUtc = nowUtc;
            LastNpcResponseUtc = nowUtc;
            NextHoldUtc = nowUtc;
            InVehicle = inVehicle;
            IsDriver = inVehicle && isDriver;
            VehicleHandle = inVehicle ? vehicleHandle : 0;
            EndReason = ConversationEndReason.None;
        }

        /// <summary>The Live session accepted setup; the player can talk now.</summary>
        public void MarkReady(DateTime nowUtc)
        {
            if (Phase == EngagementPhase.Ended) return;
            Phase = EngagementPhase.Active;
            LastActivityUtc = nowUtc;
        }

        public void NotePlayerSpeech(DateTime nowUtc)
        {
            if (Phase == EngagementPhase.Ended) return;
            LastPlayerSpeechUtc = nowUtc;
            LastActivityUtc = nowUtc;
        }

        public void NoteNpcResponse(DateTime nowUtc)
        {
            if (Phase == EngagementPhase.Ended) return;
            LastNpcResponseUtc = nowUtc;
            LastActivityUtc = nowUtc;
        }

        /// <summary>
        /// A completed turn is activity, not a reason to stop: the NPC answered,
        /// so the conversation is very much alive.
        /// </summary>
        public void NoteTurnComplete(DateTime nowUtc)
        {
            if (Phase == EngagementPhase.Ended) return;
            NpcTurns++;
            LastNpcResponseUtc = nowUtc;
            LastActivityUtc = nowUtc;
        }

        public void NoteVehicle(bool inVehicle, bool isDriver, int vehicleHandle)
        {
            if (!inVehicle) return;
            InVehicle = true;
            IsDriver = isDriver;
            if (vehicleHandle != 0) VehicleHandle = vehicleHandle;
        }

        public double ActivityAgeSeconds(DateTime nowUtc)
        {
            return (nowUtc - LastActivityUtc).TotalSeconds;
        }

        public double SilenceSincePlayerSeconds(DateTime nowUtc)
        {
            return (nowUtc - LastPlayerSpeechUtc).TotalSeconds;
        }

        /// <summary>
        /// Decides whether the conversation survives this tick and what hold to
        /// apply. Called several times a second by the plugin.
        /// </summary>
        public EngagementDecision Evaluate(DateTime nowUtc, double distanceMeters, bool npcAlive,
            bool playerAlive, bool emergency)
        {
            // Already over: report it as settled rather than "ending", or a
            // caller that polls would keep tearing the same conversation down.
            if (Phase == EngagementPhase.Ended)
                return EngagementDecision.Continue(EngagementHold.None);

            if (!playerAlive) return EndAt(nowUtc, ConversationEndReason.PlayerDied);
            if (!npcAlive) return EndAt(nowUtc, ConversationEndReason.Unavailable);
            if (emergency) return EndAt(nowUtc, ConversationEndReason.Emergency);
            if (distanceMeters > _settings.LeaveRadiusMeters) return EndAt(nowUtc, ConversationEndReason.OutOfRange);

            if (Phase == EngagementPhase.Active && ActivityAgeSeconds(nowUtc) > _settings.InactivityTimeoutSeconds)
                return EndAt(nowUtc, ConversationEndReason.Inactivity);

            if (nowUtc < NextHoldUtc) return EngagementDecision.Continue(EngagementHold.None);

            // A driver is never dragged out of the car and never told to leave;
            // the hold asks them to stay at the wheel. Anyone on foot is turned
            // back toward the player so wandering does not win the argument.
            EngagementHold hold = InVehicle ? EngagementHold.StayInVehicle : EngagementHold.FacePlayer;
            double interval = InVehicle ? _settings.HoldIntervalSeconds : _settings.FaceIntervalSeconds;
            NextHoldUtc = nowUtc.AddSeconds(Math.Max(0.2, interval));
            return EngagementDecision.Continue(hold);
        }

        public void End(DateTime nowUtc, ConversationEndReason reason)
        {
            if (Phase == EngagementPhase.Ended) return;
            Phase = EngagementPhase.Ended;
            EndReason = reason == ConversationEndReason.None ? ConversationEndReason.PlayerStopped : reason;
            EndedUtc = nowUtc;
            NextHoldUtc = DateTime.MaxValue;
        }

        private EngagementDecision EndAt(DateTime nowUtc, ConversationEndReason reason)
        {
            End(nowUtc, reason);
            return EngagementDecision.End(reason);
        }
    }

    /// <summary>
    /// The one place the spoken-language rule is written, so the Live persona
    /// and the text prompts cannot drift apart.
    /// </summary>
    public static class DialogueLanguage
    {
        public const string EnglishRule =
            "Language: always speak conversational American English, the way people actually talk in Los Santos. " +
            "Understand the player even when their English is accented, quiet, clipped or mixed with another language, " +
            "and answer in English. Never switch to Spanish or any other language on your own, never reply in a foreign " +
            "language, and never mix languages inside one reply. Keep your accent, slang, humour and personality, but keep " +
            "every word English.";

        public const string GroupEnglishRule =
            "Language: every turn is spoken in conversational American English. Nobody drifts into Spanish or any other " +
            "language and nobody mixes languages inside one line; personality, slang and humour stay, the language does not change.";
    }

    /// <summary>
    /// Dialogue is audio-only. Transcripts still exist for memory, context and
    /// debugging, but nothing spoken is ever drawn on screen, which is also what
    /// removes the missing-glyph boxes the subtitles used to show.
    /// </summary>
    public static class HudDialoguePolicy
    {
        public static bool ShowCaptions { get { return false; } }

        public static string Caption(string speaker, string text)
        {
            return null;
        }

        public static string HeardLine(string text)
        {
            return null;
        }
    }
}
