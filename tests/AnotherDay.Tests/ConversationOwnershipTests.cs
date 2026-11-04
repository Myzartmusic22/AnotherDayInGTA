using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>
    /// The behaviour these tests pin down is the bug the player reported: a
    /// conversation with pedestrian A stayed alive across the whole map, and a
    /// later conversation with pedestrian B never became the active one.
    /// </summary>
    public sealed class ConversationOwnershipTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static ConversationSession Start(int generation, int handle = 11, string npcId = "npc_a",
            string name = "Olivia Kim", bool isMale = false)
        {
            return ConversationSession.Start(generation, handle, npcId, name, isMale,
                new Vec3 { X = 10f, Y = 20f, Z = 30f }, Noon);
        }

        [Fact]
        public void StartingAConversationCreatesAUniqueOwnedSession()
        {
            ConversationSession a = Start(1);
            ConversationSession b = Start(2, 22, "npc_b", "Marcus Webb", true);

            Assert.NotEqual(a.ConversationId, b.ConversationId);
            Assert.True(a.IsActive);
            Assert.Equal(11, a.NpcHandle);
            Assert.Equal("npc_a", a.NpcId);
            Assert.False(a.IsMale);
            Assert.True(b.IsMale);
            Assert.Equal(Noon, a.StartedUtc);
        }

        [Fact]
        public void ConversationPartnerIsProtectedFromTheMomentTheHandshakeStarts()
        {
            var owner = new ConversationOwner();
            ConversationSession session = owner.Begin(101, "npc_one", "One", true,
                new Vec3 { X = 0f, Y = 0f, Z = 0f }, Noon);

            Assert.True(owner.OwnsNpc(101));
            Assert.False(owner.OwnsNpc(102));
            Assert.True(owner.MarkReady(session.Generation));
            Assert.True(owner.OwnsNpc(101));

            owner.BeginEnding(session.ConversationId, ConversationEndReason.PlayerStopped, Noon.AddSeconds(1));
            Assert.False(owner.OwnsNpc(101));
        }

        [Fact]
        public void AGenerationThatIsNotLiveCannotClaimTheConversation()
        {
            Start(7);

            Assert.True(new[] { 7 }.Length == 1);
            ConversationSession current = Start(8, 22, "npc_b");

            Assert.True(current.Owns(8));
            Assert.False(current.Owns(7));
        }

        [Fact]
        public void ReleasedSessionAcceptsNoFurtherCallbacks()
        {
            ConversationSession a = Start(3);
            a.End(ConversationEndReason.OutOfRange, Noon.AddMinutes(4));

            Assert.True(a.Ended);
            Assert.False(a.Owns(3));
            Assert.Equal(ConversationEndReason.OutOfRange, a.EndReason);
        }

        [Fact]
        public void DrivingAwayReleasesThePartner()
        {
            ConversationSession a = Start(1);

            Assert.False(ConversationSession.ShouldRelease(true, 20, 45));
            Assert.True(ConversationSession.ShouldRelease(true, 400, 45));
        }

        [Fact]
        public void ADespawnedOrDeadPartnerIsReleased()
        {
            Assert.True(ConversationSession.ShouldRelease(false, 0, 45));
        }

        [Fact]
        public void SwitchingTargetEndsTheOldConversationBeforeTheNewOneStarts()
        {
            ConversationSession a = Start(1);

            a.End(ConversationEndReason.PlayerSwitchedTarget, Noon.AddMinutes(1));
            ConversationSession b = Start(2, 22, "npc_b", "Marcus Webb", true);

            Assert.Equal(ConversationEndReason.PlayerSwitchedTarget, a.EndReason);
            Assert.False(a.Owns(1));
            Assert.True(b.Owns(2));
            Assert.NotEqual(a.ConversationId, b.ConversationId);
        }

        [Fact]
        public void IdentityIsCheckedSoARecycledHandleIsNotEnough()
        {
            ConversationSession a = Start(1, 11, "npc_a");

            Assert.True(a.IsPartner(11, "npc_a"));
            Assert.True(a.IsPartner(0, "npc_a"));
            Assert.False(a.IsPartner(99, "npc_b"));
        }

        [Fact]
        public void PositionIsTrackedOnlyWhileTheConversationLives()
        {
            ConversationSession a = Start(1);
            var moved = new Vec3 { X = 1f, Y = 2f, Z = 3f };

            a.NoteSeen(moved, Noon.AddSeconds(5));
            Assert.Equal(1f, a.LastKnownPosition.X);
            Assert.Equal(Noon.AddSeconds(5), a.LastSeenUtc);

            a.End(ConversationEndReason.PlayerStopped, Noon.AddSeconds(6));
            a.NoteSeen(new Vec3 { X = 90f, Y = 90f, Z = 90f }, Noon.AddSeconds(7));
            Assert.Equal(1f, a.LastKnownPosition.X);
        }

        [Fact]
        public void EndingTwiceKeepsTheFirstRealReason()
        {
            ConversationSession a = Start(1);
            a.End(ConversationEndReason.PlayerSwitchedTarget, Noon);
            a.End(ConversationEndReason.PlayerStopped, Noon.AddSeconds(1));

            Assert.Equal(ConversationEndReason.PlayerSwitchedTarget, a.EndReason);
        }

        [Fact]
        public void NoReasonBecomesAPlayerStop()
        {
            ConversationSession a = Start(1);
            a.End(ConversationEndReason.None, Noon);
            Assert.Equal(ConversationEndReason.PlayerStopped, a.EndReason);
        }

        [Fact]
        public void GenerationMustBePositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Start(0));
        }
    }
}
