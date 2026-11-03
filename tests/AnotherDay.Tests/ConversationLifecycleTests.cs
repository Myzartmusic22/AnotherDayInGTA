using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>
    /// The defect these tests pin down: the first pedestrian stayed the active
    /// partner after the player crossed the map, and a new pedestrian could not
    /// take the microphone. Ownership is now one object with an explicit phase
    /// and generation, so those transitions can be tested without GTA V.
    /// </summary>
    public sealed class ConversationLifecycleTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static ConversationSession Begin(ConversationOwner owner, int handle = 11,
            string npcId = "npc_a", string name = "Olivia Kim", bool isMale = false)
        {
            return owner.Begin(handle, npcId, name, isMale,
                new Vec3 { X = 10f, Y = 20f, Z = 30f }, Noon);
        }

        [Fact]
        public void ANewOwnerIsIdle()
        {
            var owner = new ConversationOwner();

            Assert.True(owner.IsIdle);
            Assert.Equal(ConversationPhase.Idle, owner.Phase);
            Assert.Null(owner.Current);
            Assert.Null(owner.ConversationId);
            Assert.False(owner.Owns(1));
        }

        [Fact]
        public void BeginningCreatesOneOwnedConnectingSession()
        {
            var owner = new ConversationOwner();
            ConversationSession session = Begin(owner);

            Assert.NotNull(session);
            Assert.Equal(ConversationPhase.Connecting, owner.Phase);
            Assert.True(owner.IsEngaged);
            Assert.Equal(session.ConversationId, owner.ConversationId);
            Assert.Equal("npc_a", owner.NpcId);
            Assert.Equal(11, owner.NpcHandle);
            Assert.True(owner.Owns(session.Generation));
        }

        [Fact]
        public void OnlyTheOwnerCanBecomeReady()
        {
            var owner = new ConversationOwner();
            ConversationSession session = Begin(owner);

            Assert.False(owner.MarkReady(session.Generation + 1));
            Assert.Equal(ConversationPhase.Connecting, owner.Phase);

            Assert.True(owner.MarkReady(session.Generation));
            Assert.Equal(ConversationPhase.Active, owner.Phase);
        }

        [Fact]
        public void ASecondConversationCannotStartUntilTheFirstIsReleased()
        {
            var owner = new ConversationOwner();
            ConversationSession first = Begin(owner);

            Assert.Null(Begin(owner, 22, "npc_b", "Marcus Webb", true));
            Assert.Equal(first.ConversationId, owner.ConversationId);

            owner.BeginEnding(first.ConversationId, ConversationEndReason.PlayerStopped, Noon.AddMinutes(1));
            owner.Release();

            ConversationSession second = Begin(owner, 22, "npc_b", "Marcus Webb", true);

            Assert.NotNull(second);
            Assert.NotEqual(first.ConversationId, second.ConversationId);
            Assert.True(second.Generation > first.Generation);
            Assert.False(owner.Owns(first.Generation));
            Assert.True(owner.Owns(second.Generation));
        }

        [Fact]
        public void EndingInvalidatesOwnershipImmediately()
        {
            var owner = new ConversationOwner();
            ConversationSession session = Begin(owner);

            ConversationSession ending = owner.BeginEnding(session.ConversationId,
                ConversationEndReason.OutOfRange, Noon.AddMinutes(3));

            Assert.NotNull(ending);
            Assert.True(ending.Ended);
            Assert.Equal(ConversationEndReason.OutOfRange, ending.EndReason);
            Assert.False(owner.Owns(session.Generation));
            Assert.Equal(ConversationPhase.Ending, owner.Phase);
            Assert.False(owner.IsEngaged);
        }

        [Fact]
        public void RepeatedTeardownIsHarmless()
        {
            var owner = new ConversationOwner();
            ConversationSession session = Begin(owner);

            Assert.NotNull(owner.BeginEnding(session.ConversationId, ConversationEndReason.PlayerStopped, Noon));
            Assert.Null(owner.BeginEnding(session.ConversationId, ConversationEndReason.PlayerStopped, Noon));
            Assert.Null(owner.BeginEnding(null, ConversationEndReason.PlayerStopped, Noon));

            owner.Release();
            owner.Release();

            Assert.True(owner.IsIdle);
            Assert.Null(owner.Current);
            Assert.Equal(ConversationEndReason.PlayerStopped, session.EndReason);
        }

        [Fact]
        public void ATeardownAimedAtTheOldPartnerCannotEndTheNewOne()
        {
            var owner = new ConversationOwner();
            ConversationSession first = Begin(owner);
            owner.BeginEnding(first.ConversationId, ConversationEndReason.PlayerSwitchedTarget, Noon);
            owner.Release();

            ConversationSession second = Begin(owner, 22, "npc_b", "Marcus Webb", true);
            owner.MarkReady(second.Generation);

            ConversationSession stale = owner.BeginEnding(first.ConversationId,
                ConversationEndReason.SessionLost, Noon.AddMinutes(2));

            Assert.Null(stale);
            Assert.Equal(ConversationPhase.Active, owner.Phase);
            Assert.True(owner.Owns(second.Generation));
            Assert.Equal(second.ConversationId, owner.ConversationId);
        }

        [Fact]
        public void AConnectionThatNeverAnswersExpires()
        {
            var owner = new ConversationOwner { ConnectTimeoutSeconds = 20 };
            Begin(owner);

            Assert.False(owner.ConnectingExpired(Noon.AddSeconds(19)));
            Assert.True(owner.ConnectingExpired(Noon.AddSeconds(20)));
        }

        [Fact]
        public void AnActiveConversationNeverExpiresAsAConnection()
        {
            var owner = new ConversationOwner { ConnectTimeoutSeconds = 20 };
            ConversationSession session = Begin(owner);
            owner.MarkReady(session.Generation);

            Assert.False(owner.ConnectingExpired(Noon.AddHours(1)));
        }

        [Fact]
        public void DeathReleasesEvenWhenThePartnerIsStillNearby()
        {
            ConversationEndReason reason;

            Assert.True(ConversationLifecyclePolicy.ShouldRelease(false, true, 1.0, 45.0, out reason));
            Assert.Equal(ConversationEndReason.PlayerDied, reason);
        }

        [Fact]
        public void ADespawnedPartnerReleasesTheConversation()
        {
            ConversationEndReason reason;

            Assert.True(ConversationLifecyclePolicy.ShouldRelease(true, false, 0.0, 45.0, out reason));
            Assert.Equal(ConversationEndReason.Unavailable, reason);
        }

        [Fact]
        public void DistanceReleasesOnlyBeyondTheLeaveRadius()
        {
            ConversationEndReason reason;

            Assert.False(ConversationLifecyclePolicy.ShouldRelease(true, true, 44.0, 45.0, out reason));
            Assert.Equal(ConversationEndReason.None, reason);

            Assert.True(ConversationLifecyclePolicy.ShouldRelease(true, true, 46.0, 45.0, out reason));
            Assert.Equal(ConversationEndReason.OutOfRange, reason);
        }

        [Theory]
        [InlineData("All right, you be safe.")]
        [InlineData("Talk to you later.")]
        [InlineData("I'm going to leave you alone.")]
        [InlineData("I gotta go.")]
        [InlineData("Take care.")]
        [InlineData("See you later.")]
        [InlineData("Have a good one.")]
        [InlineData("Goodbye.")]
        [InlineData("Thanks, I appreciate it. Take care.")]
        [InlineData("Bye.")]
        public void ARealFarewellIsRecognised(string said)
        {
            Assert.True(FarewellDetector.IsFarewell(said));
        }

        [Theory]
        [InlineData("Thanks, I appreciate it.")]
        [InlineData("Thank you.")]
        [InlineData("Who owns that car?")]
        [InlineData("Wait, one more question.")]
        [InlineData("What do you mean by that?")]
        [InlineData("I'm looking for the subway.")]
        [InlineData("")]
        [InlineData(null)]
        public void AnOrdinaryTurnIsNotAFarewell(string said)
        {
            Assert.False(FarewellDetector.IsFarewell(said));
        }

        [Fact]
        public void GratitudeAloneScoresButDoesNotEndTheConversation()
        {
            Assert.True(FarewellDetector.Score("Thank you so much") < 2);
            Assert.True(FarewellDetector.Score("Thanks, take care") >= 2);
        }

        [Fact]
        public void AQuestionAfterADepartureCueCancelsTheGoodbye()
        {
            Assert.False(FarewellDetector.IsFarewell("I should go, but where is the hospital?"));
        }
    }
}
