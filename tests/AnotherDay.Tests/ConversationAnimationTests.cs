using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public sealed class ConversationAnimationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static ConversationAnimationState NewState(int seed = 12345)
        {
            var state = new ConversationAnimationState(null, seed);
            state.SetStyle(ConversationStyle.Neutral);
            state.Reset(T0);
            return state;
        }

        [Fact]
        public void MouthOpensWhileNpcAudioPlays()
        {
            ConversationAnimationState state = NewState();
            AnimationDecision decision = state.Tick(T0.AddSeconds(1), 0.09f, true, false);

            Assert.True(decision.MouthOpen);
            Assert.Equal(ConversationAnimationPhase.NpcSpeaking, decision.Phase);
        }

        [Fact]
        public void MouthClosesAsSoonAsTheAudioStops()
        {
            ConversationAnimationState state = NewState();
            state.Tick(T0.AddSeconds(1), 0.09f, true, false);

            AnimationDecision silent = state.Tick(T0.AddSeconds(1.1), 0f, false, false);

            Assert.False(silent.MouthOpen);
            Assert.False(state.MouthOpen);
        }

        [Fact]
        public void ShortQuietPhonemesKeepTheMouthAnimationActive()
        {
            ConversationAnimationState state = NewState();
            AnimationDecision decision = state.Tick(T0.AddSeconds(1), 0.001f, true, false);

            Assert.True(decision.MouthOpen);
        }

        [Fact]
        public void NoAudioClosesMouthRegardlessOfLastChunkLevel()
        {
            ConversationAnimationState state = NewState();
            AnimationDecision decision = state.Tick(T0.AddSeconds(1), 0.09f, false, false);

            Assert.False(decision.MouthOpen);
        }

        [Fact]
        public void PlayerMouthFollowsTheMicrophone()
        {
            ConversationAnimationState state = NewState();
            AnimationDecision speaking = state.Tick(T0.AddSeconds(1), 0f, false, true);

            Assert.True(speaking.PlayerMouthOpen);
            Assert.Equal(ConversationAnimationPhase.PlayerSpeaking, speaking.Phase);

            AnimationDecision quiet = state.Tick(T0.AddSeconds(1.2), 0f, false, false);
            Assert.False(quiet.PlayerMouthOpen);
        }

        [Fact]
        public void OnlyTheCurrentSpeakerMovesTheirMouthWhenActivityOverlaps()
        {
            ConversationAnimationState state = NewState();
            AnimationDecision decision = state.Tick(T0.AddSeconds(1), 0.09f, true, true);

            Assert.False(decision.MouthOpen);
            Assert.True(decision.PlayerMouthOpen);
            Assert.Equal(ConversationAnimationPhase.PlayerSpeaking, decision.Phase);
        }

        [Fact]
        public void PlayerMouthIgnoresMicNoiseBelowConfiguredSpeechThreshold()
        {
            Assert.False(AnimationSettings.IsPlayerSpeech(0.008f, 0.02f));
            Assert.True(AnimationSettings.IsPlayerSpeech(0.02f, 0.02f));
        }

        [Fact]
        public void PlayerMouthThresholdHasANoiseResistantMinimum()
        {
            Assert.False(AnimationSettings.IsPlayerSpeech(0.004f, 0.002f));
            Assert.True(AnimationSettings.IsPlayerSpeech(0.005f, 0.002f));
        }

        [Fact]
        public void PlayerMouthClosesWithinShortReleaseWindow()
        {
            long lastSpeech = T0.Ticks;

            Assert.True(AnimationSettings.IsPlayerMouthActive(T0.AddMilliseconds(40), lastSpeech, true));
            Assert.False(AnimationSettings.IsPlayerMouthActive(T0.AddMilliseconds(60), lastSpeech, true));
            Assert.False(AnimationSettings.IsPlayerMouthActive(T0.AddMilliseconds(20), lastSpeech, false));
        }

        [Fact]
        public void NpcMouthIgnoresQuietPlaybackNoise()
        {
            Assert.False(AnimationSettings.IsNpcSpeech(0.008f));
            Assert.True(AnimationSettings.IsNpcSpeech(AnimationSettings.DefaultMouthLevel));
        }

        [Fact]
        public void ListeningProducesNodsInsteadOfTalking()
        {
            ConversationAnimationState state = NewState();

            // Nobody is talking: listening, no mouth movement.
            AnimationDecision idle = state.Tick(T0.AddSeconds(0.1), 0f, false, false);
            Assert.False(idle.MouthOpen);
            Assert.False(idle.PlayerMouthOpen);
            Assert.Equal(ConversationAnimationPhase.Listening, idle.Phase);

            // A few seconds later the NPC acknowledges what it heard.
            AnimationDecision later = state.Tick(T0.AddSeconds(6), 0f, false, false);
            Assert.Equal(ConversationGesture.Nod, later.Gesture);
        }

        [Fact]
        public void GesturesAreNotSpammedEveryTick()
        {
            ConversationAnimationState state = NewState(seed: 7);
            int gestures = 0;

            for (int i = 0; i < 60; i++)
            {
                AnimationDecision decision = state.Tick(T0.AddSeconds(1 + i * 0.25), 0.08f, true, false);
                if (decision.Gesture != ConversationGesture.None) gestures++;
            }

            // 15 seconds of continuous speech: a handful of gestures, not 60.
            Assert.InRange(gestures, 0, 4);
        }

        [Fact]
        public void AggressiveAndNervousNpcsUseDifferentGestures()
        {
            ConversationGesture[] aggressive = ConversationAnimationState.GesturesFor(new ConversationStyle
            {
                Aggression = 0.9f,
                Confidence = 0.7f
            });
            ConversationGesture[] nervous = ConversationAnimationState.GesturesFor(new ConversationStyle
            {
                Nervousness = 0.9f,
                Confidence = 0.2f
            });

            Assert.Contains(ConversationGesture.Point, aggressive);
            Assert.DoesNotContain(ConversationGesture.Point, nervous);
            Assert.Contains(ConversationGesture.HandsTogether, nervous);
        }

        [Fact]
        public void RestrainedNpcsKeepTheirGesturesSmall()
        {
            var calm = new ConversationStyle { Confidence = 0.6f, Nervousness = 0.1f };
            var nervous = new ConversationStyle { Confidence = 0.2f, Nervousness = 0.9f };

            Assert.False(calm.IsRestrained);
            Assert.True(nervous.IsRestrained);

            ConversationGesture[] pool = ConversationAnimationState.GesturesFor(nervous);
            Assert.DoesNotContain(ConversationGesture.BringItOn, pool);
            Assert.DoesNotContain(ConversationGesture.Whatever, pool);
            Assert.DoesNotContain(ConversationGesture.Point, pool);
        }

        [Fact]
        public void ExpressionFollowsPersonality()
        {
            Assert.Equal(ConversationExpression.Angry,
                ConversationAnimationState.ResolveExpression(new ConversationStyle { Aggression = 0.9f }));
            Assert.Equal(ConversationExpression.Nervous,
                ConversationAnimationState.ResolveExpression(new ConversationStyle { Nervousness = 0.8f }));
            Assert.Equal(ConversationExpression.Warm,
                ConversationAnimationState.ResolveExpression(new ConversationStyle { Friendliness = 0.8f }));
            Assert.Equal(ConversationExpression.Confident,
                ConversationAnimationState.ResolveExpression(new ConversationStyle { Confidence = 0.8f }));
        }

        [Fact]
        public void AnExplicitMoodOverridesPersonality()
        {
            var style = ConversationStyle.FromDto(new AnimationStyleDto
            {
                Aggression = 0.9f,
                Expression = "happy"
            });

            Assert.Equal(ConversationExpression.Warm, style.Expression);
            Assert.Equal(ConversationExpression.Warm, ConversationAnimationState.ResolveExpression(style));
        }

        [Fact]
        public void EndingStopsTheMouthAndAllGestures()
        {
            ConversationAnimationState state = NewState();
            state.Tick(T0.AddSeconds(1), 0.09f, true, false);

            state.End(T0.AddSeconds(2));
            AnimationDecision after = state.Tick(T0.AddSeconds(2.1), 0.09f, true, true);

            Assert.Equal(ConversationAnimationPhase.Ending, after.Phase);
            Assert.False(after.MouthOpen);
            Assert.False(after.PlayerMouthOpen);
            Assert.Equal(ConversationGesture.None, after.Gesture);
        }

        [Fact]
        public void StyleDictionaryFollowsSex()
        {
            Assert.Equal("gestures@m@standing@casual", new ConversationStyle { IsMale = true }.GestureDictionary);
            Assert.Equal("gestures@f@standing@casual", new ConversationStyle { IsMale = false }.GestureDictionary);
        }
    }

    public sealed class PlaybackSpeechMonitorTests
    {
        [Fact]
        public void SpeechIsActiveOnlyForTheCurrentRenderedAudioBlock()
        {
            var monitor = new PlaybackSpeechMonitor();
            float[] voicedBlock = { 0.1f, -0.1f, 0.08f, -0.08f };
            float[] silentBlock = { 0f, 0f, 0f, 0f };

            monitor.Record(voicedBlock, 0, voicedBlock.Length);
            Assert.True(monitor.IsSpeaking);

            monitor.Record(silentBlock, 0, silentBlock.Length);
            Assert.False(monitor.IsSpeaking);
        }

        [Fact]
        public void MonitorUsesOnlyTheRequestedSampleRange()
        {
            var monitor = new PlaybackSpeechMonitor();
            float[] samples = { 0.5f, 0f, 0f };

            monitor.Record(samples, 1, 2);

            Assert.False(monitor.IsSpeaking);
        }

        [Fact]
        public void EmptyAndResetBlocksClearSpeechActivity()
        {
            var monitor = new PlaybackSpeechMonitor();
            float[] voicedBlock = { 0.1f, -0.1f };

            monitor.Record(voicedBlock, 0, voicedBlock.Length);
            Assert.True(monitor.IsSpeaking);

            monitor.Record(voicedBlock, 0, 0);
            Assert.False(monitor.IsSpeaking);

            monitor.Record(voicedBlock, 0, voicedBlock.Length);
            monitor.Reset();
            Assert.False(monitor.IsSpeaking);
        }
    }

    public sealed class AnimationSafetyTests
    {
        [Fact]
        public void BodyAnimationIsRefusedInAnyVehicle()
        {
            Assert.False(AnimationSafety.CanAnimateBody(false, false, true, false, false, false, false, false));
            Assert.False(AnimationSafety.CanAnimateBody(false, false, false, true, false, false, false, false));
        }

        [Fact]
        public void FaceAnimationIsStillAllowedInAVehicle()
        {
            // Talking in a car is normal; body gestures are not.
            Assert.True(AnimationSafety.CanAnimateFace(false, false));
        }

        [Fact]
        public void DeadOrRagdolledPedsGetNoAnimationAtAll()
        {
            Assert.False(AnimationSafety.CanAnimateFace(true, false));
            Assert.False(AnimationSafety.CanAnimateFace(false, true));
            Assert.False(AnimationSafety.CanAnimateBody(true, false, false, false, false, false, false, false));
        }

        [Fact]
        public void FallingSwimmingAndCombatBlockBodyAnimation()
        {
            Assert.False(AnimationSafety.CanAnimateBody(false, false, false, false, true, false, false, false));
            Assert.False(AnimationSafety.CanAnimateBody(false, false, false, false, false, true, false, false));
            Assert.False(AnimationSafety.CanAnimateBody(false, false, false, false, false, false, true, false));
            Assert.False(AnimationSafety.CanAnimateBody(false, false, false, false, false, false, false, true));
        }

        [Fact]
        public void StandingIdlePedCanBeAnimated()
        {
            Assert.True(AnimationSafety.CanAnimateBody(false, false, false, false, false, false, false, false));
        }

        [Fact]
        public void PlayerGetsNoBodyAnimationWhileDrivingOrRiding()
        {
            Assert.False(AnimationSafety.CanAnimatePlayerBody(false, true, false, false, false));
            Assert.True(AnimationSafety.CanAnimatePlayerBody(false, false, false, false, false));
        }
    }

    public sealed class VehicleTransitionPolicyTests
    {
        [Fact]
        public void EnteringACarDuringAConversationEndsIt()
        {
            Assert.True(VehicleTransitionPolicy.EndsConversationOnEntry(true, false, true));
        }

        [Fact]
        public void AlreadyBeingInACarDoesNotEndItAgain()
        {
            Assert.False(VehicleTransitionPolicy.EndsConversationOnEntry(true, true, true));
        }

        [Fact]
        public void AnInactiveConversationIsNotAffected()
        {
            Assert.False(VehicleTransitionPolicy.EndsConversationOnEntry(false, false, true));
        }

        [Fact]
        public void ConversationsCanBeStartedFromACarOrBicycle()
        {
            Assert.True(VehicleTransitionPolicy.CanStartWhileInVehicle(true));
        }
    }

    public sealed class GameThreadQueueTests
    {
        [Fact]
        public void PostedWorkRunsInOrderOnDrain()
        {
            var queue = new GameThreadQueue();
            var order = new System.Collections.Generic.List<int>();

            queue.Post(() => order.Add(1));
            queue.Post(() => order.Add(2));
            queue.Post(() => order.Add(3));

            Assert.Equal(0, queue.Handled);
            queue.Drain();

            Assert.Equal(new[] { 1, 2, 3 }, order);
            Assert.Equal(3, queue.Handled);
        }

        [Fact]
        public void AFailingActionDoesNotStopTheRest()
        {
            var queue = new GameThreadQueue();
            bool ran = false;

            queue.Post(() => { throw new InvalidOperationException("boom"); });
            queue.Post(() => ran = true);

            int handled = queue.Drain();

            Assert.Equal(2, handled);
            Assert.True(ran);
            Assert.NotNull(queue.LastError);
            Assert.Equal("boom", queue.LastError.Message);
        }

        [Fact]
        public void DrainIsBoundedPerTick()
        {
            var queue = new GameThreadQueue();
            for (int i = 0; i < 5; i++) queue.Post(() => { });

            Assert.Equal(2, queue.Drain(2));
            Assert.Equal(3, queue.Count);
        }
    }
}
