using System;

namespace AnotherDay.Shared
{
    /// <summary>Where the one active conversation is in its life cycle.</summary>
    public enum ConversationPhase
    {
        /// <summary>No conversation owns the microphone, the socket or an NPC.</summary>
        Idle,

        /// <summary>An NPC has been chosen and the socket is being opened.</summary>
        Connecting,

        /// <summary>The player can talk; this conversation owns everything.</summary>
        Active,

        /// <summary>Teardown has begun; the generation is already invalid.</summary>
        Ending
    }

    /// <summary>
    /// The single authoritative owner of the one conversation the player can be
    /// having. Everything else — microphone forwarding, Gemini audio, animation,
    /// the HUD and the talk key — asks this object who the partner is rather than
    /// remembering one of their own.
    ///
    /// The bug this replaces: ownership lived in a scattering of fields
    /// (<c>_liveActive</c>, <c>_liveStarting</c>, <c>_liveNpcHandle</c>), and the
    /// only code that released them ran behind a guard that could never be
    /// satisfied. A conversation that never reached "ready" — a handshake the
    /// service did not answer — therefore kept the talk key swallowed forever,
    /// and pressing P beside a new pedestrian only flushed the old NPC's audio.
    ///
    /// Two rules make the stuck state impossible:
    /// 1. <see cref="Begin"/> refuses to start while another conversation owns the
    ///    slot, so a new partner can never inherit an old session by accident.
    /// 2. <see cref="BeginEnding"/> invalidates the generation on the calling
    ///    thread, so every late callback fails <see cref="Owns"/> immediately,
    ///    before any game-thread work runs.
    /// </summary>
    public sealed class ConversationOwner
    {
        private readonly object _sync = new object();
        private ConversationSession _current;
        private ConversationPhase _phase = ConversationPhase.Idle;
        private int _generation;

        /// <summary>
        /// How long a conversation may sit in <see cref="ConversationPhase.Connecting"/>
        /// before it is treated as failed. Without this, a service that never
        /// answers leaves the player unable to talk to anybody.
        /// </summary>
        public double ConnectTimeoutSeconds { get; set; } = 20.0;

        public ConversationPhase Phase { get { lock (_sync) return _phase; } }

        public ConversationSession Current { get { lock (_sync) return _current; } }

        public int Generation { get { lock (_sync) return _generation; } }

        public bool IsIdle { get { lock (_sync) return _phase == ConversationPhase.Idle; } }

        public bool IsEngaged
        {
            get { lock (_sync) return _phase == ConversationPhase.Connecting || _phase == ConversationPhase.Active; }
        }

        /// <summary>Atomically checks whether the given pedestrian currently owns the conversation slot.</summary>
        public bool OwnsNpc(int npcHandle)
        {
            lock (_sync)
            {
                return npcHandle > 0 &&
                       (_phase == ConversationPhase.Connecting || _phase == ConversationPhase.Active) &&
                       _current != null && _current.NpcHandle == npcHandle;
            }
        }

        public bool IsConnecting { get { lock (_sync) return _phase == ConversationPhase.Connecting; } }

        public string ConversationId { get { lock (_sync) return _current == null ? null : _current.ConversationId; } }

        public string NpcId { get { lock (_sync) return _current == null ? null : _current.NpcId; } }

        public string DisplayName { get { lock (_sync) return _current == null ? null : _current.DisplayName; } }

        public int NpcHandle { get { lock (_sync) return _current == null ? 0 : _current.NpcHandle; } }

        /// <summary>
        /// Claims the slot for a new partner. Returns null when a previous
        /// conversation has not been released yet: callers must finish that
        /// teardown first rather than let two partners share one session.
        /// </summary>
        public ConversationSession Begin(int npcHandle, string npcId, string displayName, bool isMale,
            Vec3 position, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_phase != ConversationPhase.Idle) return null;

                _generation++;
                _current = ConversationSession.Start(_generation, npcHandle, npcId, displayName, isMale,
                    position, nowUtc);
                _phase = ConversationPhase.Connecting;
                return _current;
            }
        }

        /// <summary>The service accepted setup. Only the owning generation may say so.</summary>
        public bool MarkReady(int generation)
        {
            lock (_sync)
            {
                if (!OwnsLocked(generation) || _phase != ConversationPhase.Connecting) return false;
                _phase = ConversationPhase.Active;
                return true;
            }
        }

        /// <summary>
        /// True only for the live conversation. A reconnected pipe, an answer that
        /// arrives after the player walked away, or a second conversation opened
        /// in the same session all fail this test.
        /// </summary>
        public bool Owns(int generation)
        {
            lock (_sync) return OwnsLocked(generation);
        }

        /// <summary>A connection that never produced a session has failed.</summary>
        public bool ConnectingExpired(DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_phase != ConversationPhase.Connecting || _current == null) return false;
                return (nowUtc - _current.StartedUtc).TotalSeconds >= ConnectTimeoutSeconds;
            }
        }

        /// <summary>
        /// Begins teardown for one specific conversation and returns it. Passing
        /// the id means a request aimed at an old partner can never end the one
        /// that replaced it. Idempotent: the second call returns null.
        /// </summary>
        public ConversationSession BeginEnding(string conversationId, ConversationEndReason reason, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_phase == ConversationPhase.Idle || _phase == ConversationPhase.Ending) return null;
                if (_current == null) return null;
                if (conversationId != null &&
                    !string.Equals(_current.ConversationId, conversationId, StringComparison.Ordinal))
                    return null;

                _phase = ConversationPhase.Ending;
                _current.End(reason, nowUtc);
                return _current;
            }
        }

        /// <summary>Completes the release and returns to idle. Idempotent.</summary>
        public void Release()
        {
            lock (_sync)
            {
                _current = null;
                _phase = ConversationPhase.Idle;
            }
        }

        private bool OwnsLocked(int generation)
        {
            return _phase != ConversationPhase.Idle && _phase != ConversationPhase.Ending &&
                   _current != null && _current.Owns(generation);
        }
    }

    /// <summary>
    /// The one release rule for the life cycle, written separately from the
    /// plugin so the death/respawn and distance cases can be tested without
    /// GTA V. It deliberately does not consult the engagement state: a partner
    /// that despawned or a player who died must release the conversation even if
    /// the richer engagement simulation never produced a decision.
    /// </summary>
    public static class ConversationLifecyclePolicy
    {
        /// <summary>
        /// A detected farewell is not complete until the model's turn is done
        /// and its queued audio has drained. Releasing sooner cuts off the
        /// character's closing line.
        /// </summary>
        public static bool ShouldFinishFarewell(bool farewellPending, bool turnComplete, bool audioPlaying)
        {
            return farewellPending && turnComplete && !audioPlaying;
        }

        public static bool ShouldRelease(bool playerAlive, bool npcValid, double distanceMeters,
            double leaveRadiusMeters, out ConversationEndReason reason)
        {
            reason = ConversationEndReason.None;
            if (!playerAlive)
            {
                reason = ConversationEndReason.PlayerDied;
                return true;
            }
            if (!npcValid)
            {
                reason = ConversationEndReason.Unavailable;
                return true;
            }
            if (leaveRadiusMeters > 0 && distanceMeters > leaveRadiusMeters)
            {
                reason = ConversationEndReason.OutOfRange;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Recognises a spoken farewell in a completed conversation turn.
    ///
    /// This is intentionally not exact string matching: the transcript is
    /// normalised, weighted phrases are scored, and a question or a "but"/"wait"
    /// continuation cancels the goodbye. Gratitude alone ("thanks", "thank you")
    /// deliberately does not end a conversation, because people say it in the
    /// middle of one; gratitude combined with a departure cue does.
    /// </summary>
    public static class FarewellDetector
    {
        private static readonly string[] DepartureCues =
        {
            "goodbye", "good bye", "bye bye", "bye",
            "see you later", "see you around", "see you soon", "see you", "see ya",
            "talk to you later", "talk later", "catch you later", "speak later",
            "i gotta go", "i got to go", "i have to go", "i've got to go",
            "i need to go", "i should go", "i'm going to go", "i am going to go",
            "i'll be going", "i will be going", "time for me to go", "time to go",
            "take care", "take care of yourself", "take it easy", "be safe", "stay safe",
            "you be safe", "stay safe out there",
            "have a good one", "have a good day", "have a nice day", "good night", "goodnight",
            "i'm heading out", "i am heading out", "heading out", "i'm out",
            "i'm leaving", "i am leaving", "i'm gonna head out", "i am going to head out",
            "i gotta run", "i have to run", "i need to get going", "i'll leave you",
            "leave you alone", "leave you be",
            "nice talking to you", "good talking to you", "enjoy your day"
        };

        private static readonly string[] GratitudeCues =
        {
            "thank you", "thanks", "thank ya", "appreciate it", "appreciate that", "i appreciate"
        };

        private static readonly string[] ContinuingCues =
        {
            "but", "wait", "hold on", "hang on", "one more", "quick question",
            "before i go", "real quick", "actually", "however", "?"
        };

        /// <summary>Weighted score: departures are decisive, thanks alone are not.</summary>
        public static int Score(string text)
        {
            string normalized = Normalize(text);
            if (normalized.Length == 0) return 0;

            int score = 0;
            bool departure = false;
            foreach (string cue in DepartureCues)
            {
                if (normalized.IndexOf(cue, StringComparison.Ordinal) < 0) continue;
                departure = true;
                score += 2;
                break;
            }

            if (!departure) return 0;

            foreach (string cue in GratitudeCues)
            {
                if (normalized.IndexOf(cue, StringComparison.Ordinal) < 0) continue;
                score += 1;
                break;
            }

            foreach (string cue in ContinuingCues)
            {
                if (normalized.IndexOf(cue, StringComparison.Ordinal) < 0) continue;
                score -= 2;
                break;
            }

            return score;
        }

        public static bool IsFarewell(string text)
        {
            return Score(text) >= 2;
        }

        /// <summary>
        /// Lower case, punctuation flattened to spaces, whitespace collapsed.
        /// Apostrophes are kept as typed so "i'm" stays one word; the cue table
        /// spells contractions both ways where it matters.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            foreach (char raw in text)
            {
                char c = char.ToLowerInvariant(raw);
                if (char.IsLetterOrDigit(c) || c == '\'' || c == '?')
                {
                    builder.Append(c);
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            return builder.ToString().Trim();
        }
    }
}
