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
    ///                                |                \--release w/ speech--> AwaitingReply
    ///                                \--no speech, window closed--> Open --press--> Holding
    ///   Open/AwaitingReply --press--> Holding --release w/ speech--> AwaitingReply
    ///                                           \--release silent--> Open
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
        /// <summary>The first listening window: the microphone is open without another press.</summary>
        Listening,
        /// <summary>Push-to-talk is held in an established conversation.</summary>
        Holding,
        /// <summary>Conversation established, microphone closed until the next press.</summary>
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
    /// The whole interaction contract for one key: first press starts the
    /// conversation and opens the microphone, later presses are push-to-talk,
    /// and the microphone is submitted on release or shortly after the player
    /// stops talking so a tap cannot leave a turn hanging.
    /// </summary>
    public sealed class TalkStateMachine
    {
        /// <summary>Silence after real speech that finishes the automatically opened turn.</summary>
        public const double SilenceSubmitSeconds = 1.1;

        /// <summary>How long the first press keeps the microphone open when nothing is said.</summary>
        public const double ListenWindowSeconds = 20.0;

        public TalkPhase Phase { get; private set; } = TalkPhase.Idle;

        /// <summary>The microphone may only be forwarded in these phases.</summary>
        public bool ShouldStream => Phase == TalkPhase.Listening || Phase == TalkPhase.Holding;

        public bool ConversationActive => Phase != TalkPhase.Idle;

        private bool _speech;
        private DateTime? _lastSpeechUtc;
        private DateTime _listenOpenedUtc;

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
            if (Phase != TalkPhase.Holding && Phase != TalkPhase.Listening) return TalkAction.None;

            // A silent tap is not a sentence. Submitting it would ask Gemini to
            // answer nothing; the conversation simply stays open for the next one.
            if (!_speech)
            {
                Phase = TalkPhase.Open;
                return TalkAction.None;
            }

            return Submit();
        }

        /// <summary>
        /// The voice session answered. The first listening window opens here, so
        /// a press that was released during a slow handshake still gets heard.
        /// </summary>
        public TalkAction Ready(DateTime now)
        {
            if (Phase != TalkPhase.Connecting) return TalkAction.None;

            Phase = TalkPhase.Listening;
            ClearSpeech();
            _listenOpenedUtc = now;
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
        /// Clock. Ends the automatic first turn once the player has stopped
        /// talking, and closes an unused listening window so the microphone is
        /// not left open indefinitely.
        /// </summary>
        public TalkAction Tick(DateTime now)
        {
            if (Phase != TalkPhase.Listening) return TalkAction.None;

            if (_lastSpeechUtc.HasValue)
            {
                if ((now - _lastSpeechUtc.Value).TotalSeconds >= SilenceSubmitSeconds) return Submit();
                return TalkAction.None;
            }

            if ((now - _listenOpenedUtc).TotalSeconds >= ListenWindowSeconds)
            {
                Phase = TalkPhase.Open;
                return TalkAction.None;
            }

            return TalkAction.None;
        }

        /// <summary>The NPC's reply has finished playing; the microphone is closed until asked for.</summary>
        public void ReplyFinished()
        {
            if (Phase == TalkPhase.AwaitingReply) Phase = TalkPhase.Open;
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
