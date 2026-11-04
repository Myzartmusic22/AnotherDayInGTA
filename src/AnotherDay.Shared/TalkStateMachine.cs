using System;

namespace AnotherDay.Shared
{
    /// <summary>
    /// What the talk key is doing, as one explicit state instead of a set of
    /// booleans. The defect this replaces was two overlapping flags
    /// (starting/active plus held) which could not express "the player pressed
    /// once and the connection is still opening", so the first press appeared to
    /// do nothing and a second press was needed to actually speak.
    ///
    /// The conversation is started by the press itself:
    ///
    ///   Idle --press--> Connecting --ready--> Listening --speech+silence--> AwaitingReply
    ///                                               ^                          |
    ///                                               |---- reply complete ------|
    ///   AwaitingReply --press--> Holding --release with speech--> AwaitingReply
    ///                                      \--release silent--> Listening
    ///
    /// No native call and no clock lives here, so every transition is testable
    /// without a game process.
    /// </summary>
    public enum TalkPhase
    {
        /// <summary>No conversation.</summary>
        Idle,
        /// <summary>A press asked for a conversation; the voice session is opening.</summary>
        Connecting,
        /// <summary>The conversation microphone is open unless the NPC is speaking.</summary>
        Listening,
        /// <summary>The player is deliberately interrupting an NPC response.</summary>
        Holding,
        /// <summary>Reserved for an established conversation with listening intentionally paused.</summary>
        Open,
        /// <summary>A turn was submitted; the NPC has the floor.</summary>
        AwaitingReply
    }

    public enum TalkAction
    {
        None,
        /// <summary>Select the nearest pedestrian and open a voice session.</summary>
        StartConversation,
        /// <summary>Player took the floor in an existing conversation.</summary>
        BeginTurn,
        /// <summary>The player's sentence is finished and must be sent as a turn.</summary>
        SubmitTurn
    }

    /// <summary>
    /// The whole interaction contract for one key: the first press starts an
    /// open-mic conversation, speech ends automatically after silence, and the
    /// key can still be used to interrupt an NPC response.
    /// </summary>
    public sealed class TalkStateMachine
    {
        /// <summary>Silence after real speech that finishes the automatically opened turn.</summary>
        public const double SilenceSubmitSeconds = 1.1;

        public TalkPhase Phase { get; private set; } = TalkPhase.Idle;

        /// <summary>The microphone may only be forwarded in these phases.</summary>
        public bool ShouldStream => Phase == TalkPhase.Listening || Phase == TalkPhase.Holding;

        public bool ConversationActive => Phase != TalkPhase.Idle;

        private bool _speech;
        private DateTime? _lastSpeechUtc;

        public TalkAction Press(DateTime now)
        {
            switch (Phase)
            {
                case TalkPhase.Idle:
                    Phase = TalkPhase.Connecting;
                    ClearSpeech();
                    return TalkAction.StartConversation;

                // The press that started the handshake is still the same press.
                // Asking for a second conversation here is what made the player
                // press twice before anyone answered.
                case TalkPhase.Connecting:
                    return TalkAction.None;

                case TalkPhase.Listening:
                case TalkPhase.Holding:
                    return TalkAction.None;

                default: // Open, AwaitingReply: the player is interrupting or starting their turn.
                    Phase = TalkPhase.Holding;
                    ClearSpeech();
                    return TalkAction.BeginTurn;
            }
        }

        public TalkAction Release(DateTime now)
        {
            if (Phase != TalkPhase.Holding) return TalkAction.None;

            // A silent interruption is not a sentence. Resume hands-free
            // listening instead of submitting an empty turn or closing the mic.
            if (!_speech)
            {
                Phase = TalkPhase.Listening;
                return TalkAction.None;
            }

            return Submit();
        }

        /// <summary>
        /// The voice session answered. Hands-free listening opens here, so a
        /// press released during a slow handshake still starts the conversation.
        /// </summary>
        public TalkAction Ready(DateTime now)
        {
            if (Phase != TalkPhase.Connecting) return TalkAction.None;

            Phase = TalkPhase.Listening;
            ClearSpeech();
            return TalkAction.None;
        }

        /// <summary>The handshake failed: no conversation, and the next press starts a new one.</summary>
        public void Failed()
        {
            Phase = TalkPhase.Idle;
            ClearSpeech();
        }

        /// <summary>Real speech was measured in the microphone.</summary>
        public void NoteSpeech(DateTime now)
        {
            if (Phase != TalkPhase.Listening && Phase != TalkPhase.Holding) return;
            if (!_speech) _speech = true;
            _lastSpeechUtc = now;
        }

        /// <summary>
        /// Clock. Ends the current turn once the player has stopped talking.
        /// The microphone stays open for the life of the conversation.
        /// </summary>
        public TalkAction Tick(DateTime now)
        {
            if (Phase != TalkPhase.Listening) return TalkAction.None;

            if (_lastSpeechUtc.HasValue)
            {
                if ((now - _lastSpeechUtc.Value).TotalSeconds >= SilenceSubmitSeconds) return Submit();
                return TalkAction.None;
            }

            return TalkAction.None;
        }

        /// <summary>The NPC's reply has finished playing; resume hands-free listening.</summary>
        public void ReplyFinished()
        {
            if (Phase != TalkPhase.AwaitingReply) return;
            ClearSpeech();
            Phase = TalkPhase.Listening;
        }

        /// <summary>The conversation ended or was torn down for any reason.</summary>
        public void Reset()
        {
            Phase = TalkPhase.Idle;
            ClearSpeech();
        }

        private TalkAction Submit()
        {
            Phase = TalkPhase.AwaitingReply;
            ClearSpeech();
            return TalkAction.SubmitTurn;
        }

        private void ClearSpeech()
        {
            _speech = false;
            _lastSpeechUtc = null;
        }
    }
}
