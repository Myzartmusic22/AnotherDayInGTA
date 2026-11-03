using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public class TalkStateMachineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FirstPressStartsTheConversation()
        {
            var talk = new TalkStateMachine();
            Assert.Equal(TalkAction.StartConversation, talk.Press(T0));
            Assert.Equal(TalkPhase.Connecting, talk.Phase);
            Assert.True(talk.ConversationActive);
        }

        [Fact]
        public void SecondPressWhileConnectingDoesNotStartAnotherConversation()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            Assert.Equal(TalkAction.None, talk.Press(T0.AddMilliseconds(200)));
            Assert.Equal(TalkPhase.Connecting, talk.Phase);
        }

        [Fact]
        public void ReadyOpensTheMicrophoneWithoutAnotherPress()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            Assert.Equal(TalkAction.None, talk.Ready(T0.AddSeconds(1)));
            Assert.Equal(TalkPhase.Listening, talk.Phase);
            Assert.True(talk.ShouldStream);
        }

        [Fact]
        public void FirstPressReleasedDuringHandshakeStillGetsHeard()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            Assert.Equal(TalkAction.None, talk.Release(T0.AddMilliseconds(50)));
            // The session answers after the key is already up.
            talk.Ready(T0.AddSeconds(2));
            Assert.Equal(TalkPhase.Listening, talk.Phase);

            talk.NoteSpeech(T0.AddSeconds(3));
            Assert.Equal(TalkAction.SubmitTurn, talk.Tick(T0.AddSeconds(4.5)));
        }

        [Fact]
        public void AutomaticTurnSubmitsAfterSpeechAndSilence()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0.AddSeconds(1));

            talk.NoteSpeech(T0.AddSeconds(2));
            Assert.Equal(TalkAction.None, talk.Tick(T0.AddSeconds(2.5)));
            Assert.Equal(TalkAction.SubmitTurn, talk.Tick(T0.AddSeconds(3.2)));
            Assert.Equal(TalkPhase.AwaitingReply, talk.Phase);
            Assert.False(talk.ShouldStream);
        }

        [Fact]
        public void AThinkingPauseDoesNotCutTheSentenceOff()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);

            talk.NoteSpeech(T0.AddSeconds(1));
            talk.NoteSpeech(T0.AddSeconds(2.4)); // player paused, then continued
            Assert.Equal(TalkAction.None, talk.Tick(T0.AddSeconds(3.0)));
            Assert.Equal(TalkAction.SubmitTurn, talk.Tick(T0.AddSeconds(3.6)));
        }

        [Fact]
        public void UnusedListeningWindowClosesInsteadOfHoldingTheMicrophoneForever()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);

            Assert.Equal(TalkAction.None, talk.Tick(T0.AddSeconds(TalkStateMachine.ListenWindowSeconds + 1)));
            Assert.Equal(TalkPhase.Open, talk.Phase);
            Assert.False(talk.ShouldStream);
            Assert.True(talk.ConversationActive);
        }

        [Fact]
        public void SilentReleaseDoesNotSubmitAnEmptyTurn()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);

            Assert.Equal(TalkAction.None, talk.Release(T0.AddMilliseconds(120)));
            Assert.Equal(TalkPhase.Open, talk.Phase);
        }

        [Fact]
        public void LaterPressIsPushToTalkAndReleaseSubmits()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.Release(T0.AddMilliseconds(100)); // silent tap closes the window

            Assert.Equal(TalkAction.BeginTurn, talk.Press(T0.AddSeconds(10)));
            Assert.Equal(TalkPhase.Holding, talk.Phase);
            Assert.True(talk.ShouldStream);

            talk.NoteSpeech(T0.AddSeconds(11));
            Assert.Equal(TalkAction.SubmitTurn, talk.Release(T0.AddSeconds(12)));
            Assert.Equal(TalkPhase.AwaitingReply, talk.Phase);
        }

        [Fact]
        public void PressingWhileTheNpcSpeaksInterruptsIt()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.NoteSpeech(T0.AddSeconds(1));
            talk.Tick(T0.AddSeconds(2.2)); // silence ends the turn: the NPC has the floor

            Assert.Equal(TalkPhase.AwaitingReply, talk.Phase);
            Assert.Equal(TalkAction.BeginTurn, talk.Press(T0.AddSeconds(3)));
            Assert.Equal(TalkPhase.Holding, talk.Phase);
        }

        [Fact]
        public void ReplyFinishingReturnsToOpenWithoutLosingTheConversation()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.NoteSpeech(T0.AddSeconds(1));
            talk.Tick(T0.AddSeconds(2.2));

            talk.ReplyFinished();
            Assert.Equal(TalkPhase.Open, talk.Phase);
            Assert.True(talk.ConversationActive);
            Assert.False(talk.ShouldStream);
        }

        [Fact]
        public void AHeldTurnIsSubmittedOnRelease()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.Release(T0.AddMilliseconds(100));

            talk.Press(T0.AddSeconds(5));
            talk.NoteSpeech(T0.AddSeconds(9));
            Assert.Equal(TalkAction.SubmitTurn, talk.Release(T0.AddSeconds(9.5)));
        }

        [Fact]
        public void RepeatedKeyDownWhileHoldingIsIgnored()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.Release(T0.AddMilliseconds(100));
            talk.Press(T0.AddSeconds(4));

            Assert.Equal(TalkAction.None, talk.Press(T0.AddSeconds(4.1)));
            Assert.Equal(TalkPhase.Holding, talk.Phase);
        }

        [Fact]
        public void FailedHandshakeReturnsToIdleSoTheNextPressWorks()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Failed();
            Assert.Equal(TalkPhase.Idle, talk.Phase);
            Assert.Equal(TalkAction.StartConversation, talk.Press(T0.AddSeconds(1)));
        }

        [Fact]
        public void ResetEndsEverything()
        {
            var talk = new TalkStateMachine();
            talk.Press(T0);
            talk.Ready(T0);
            talk.NoteSpeech(T0.AddSeconds(1));
            talk.Reset();

            Assert.Equal(TalkPhase.Idle, talk.Phase);
            Assert.False(talk.ConversationActive);
            Assert.False(talk.ShouldStream);
        }

        [Fact]
        public void SpeechOutsideAConversationIsIgnored()
        {
            var talk = new TalkStateMachine();
            talk.NoteSpeech(T0);
            Assert.Equal(TalkAction.None, talk.Tick(T0.AddSeconds(5)));
            Assert.Equal(TalkPhase.Idle, talk.Phase);
        }
    }
}
