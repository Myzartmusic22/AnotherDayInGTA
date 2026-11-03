using System;

namespace AnotherDay.Shared
{
    /// <summary>
    /// Ownership record for exactly one player-to-NPC conversation.
    ///
    /// The bug this exists to prevent: a conversation opened with pedestrian A
    /// kept answering after the player had driven across the city, and a later
    /// P press beside pedestrian B went nowhere because A still "owned" the
    /// microphone, the audio link and the animation. Ownership is therefore an
    /// explicit, generation-tagged object rather than a handful of booleans:
    /// every callback carries the generation it belongs to, and a callback whose
    /// generation is not the live one is discarded instead of applied to
    /// whoever happens to be in front of the player now.
    /// </summary>
    public sealed class ConversationSession
    {
        private ConversationSession()
        {
        }

        /// <summary>Unique id for this conversation. Sent to the service so an
        /// answer can be matched to the conversation that asked for it.</summary>
        public string ConversationId { get; private set; }

        /// <summary>Monotonic counter. Every start bumps it; stale callbacks
        /// are recognised by a generation that no longer matches.</summary>
        public int Generation { get; private set; }

        /// <summary>The GTA entity handle at the time the conversation started.
        /// Handles are recycled, so this is never trusted without the identity.</summary>
        public int NpcHandle { get; set; }

        /// <summary>Persistent identity; survives respawns and handle reuse.</summary>
        public string NpcId { get; private set; }

        public string DisplayName { get; private set; }

        /// <summary>Live model classification, used to validate the voice.</summary>
        public bool IsMale { get; private set; }

        public DateTime StartedUtc { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        public Vec3 LastKnownPosition { get; private set; }

        public bool Ended { get; private set; }
        public ConversationEndReason EndReason { get; private set; }
        public DateTime EndedUtc { get; private set; }

        public bool IsActive { get { return !Ended; } }

        public static ConversationSession Start(int generation, int npcHandle, string npcId,
            string displayName, bool isMale, Vec3 position, DateTime nowUtc)
        {
            if (generation <= 0) throw new ArgumentOutOfRangeException("generation");

            return new ConversationSession
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                Generation = generation,
                NpcHandle = npcHandle,
                NpcId = npcId,
                DisplayName = displayName,
                IsMale = isMale,
                StartedUtc = nowUtc,
                LastSeenUtc = nowUtc,
                LastKnownPosition = position,
                EndReason = ConversationEndReason.None
            };
        }

        /// <summary>
        /// True only for the live conversation. A pipe reader that reconnects, a
        /// network answer that arrives after the player walked away, or a second
        /// conversation opened in the same session all fail this test.
        /// </summary>
        public bool Owns(int generation)
        {
            return !Ended && generation == Generation;
        }

        /// <summary>Identity check that does not trust a recycled entity handle.</summary>
        public bool IsPartner(int handle, string npcId)
        {
            if (handle != 0 && handle == NpcHandle) return true;
            return !string.IsNullOrEmpty(npcId) && !string.IsNullOrEmpty(NpcId) &&
                   string.Equals(npcId, NpcId, StringComparison.OrdinalIgnoreCase);
        }

        public void NoteSeen(Vec3 position, DateTime nowUtc)
        {
            if (Ended) return;
            if (position != null) LastKnownPosition = position;
            LastSeenUtc = nowUtc;
        }

        public void End(ConversationEndReason reason, DateTime nowUtc)
        {
            if (Ended) return;
            Ended = true;
            EndReason = reason == ConversationEndReason.None ? ConversationEndReason.PlayerStopped : reason;
            EndedUtc = nowUtc;
        }

        public double AgeSeconds(DateTime nowUtc)
        {
            return (nowUtc - StartedUtc).TotalSeconds;
        }

        /// <summary>
        /// A conversation ends the moment its partner is no longer a real,
        /// reachable person: despawned, dead, or further away than the player can
        /// plausibly still be talking to them.
        /// </summary>
        public static bool ShouldRelease(bool npcAvailable, double distanceMeters, double leaveRadiusMeters)
        {
            if (!npcAvailable) return true;
            if (leaveRadiusMeters <= 0) return false;
            return distanceMeters > leaveRadiusMeters;
        }
    }
}
