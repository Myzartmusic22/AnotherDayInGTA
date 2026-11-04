using System;
using System.Collections.Generic;
using AnotherDay.Service.Domain;
using AnotherDay.Service.Live;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>
    /// The rules that decide whether an NPC keeps talking to the player. These
    /// are the failures that made conversations collapse after one line, so they
    /// are pinned here instead of depending on a live session in the game.
    /// </summary>
    public sealed class ConversationEngagementTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ConversationTargetPolicyLeavesRunningOrThreatenedPedsAlone()
        {
            Assert.True(ConversationTargetPolicy.CanStart(false, false, false, false));
            Assert.False(ConversationTargetPolicy.CanStart(true, false, false, false));
            Assert.False(ConversationTargetPolicy.CanStart(false, true, false, false));
            Assert.False(ConversationTargetPolicy.CanStart(false, false, true, false));
            Assert.False(ConversationTargetPolicy.CanStart(false, false, false, true));
        }

        [Fact]
        public void GeneratedVoiceStyleUsesRoleAndPlaceNotDemographicAccentLabels()
        {
            var profile = new NpcProfile
            {
                Category = NpcCategories.Gang,
                Neighborhood = "Sandy Shores",
                Personality = new PersonalityTraits(),
                Speech = new SpeechStyle
                {
                    Accent = "African-American vernacular",
                    Vocabulary = "formal"
                }
            };

            string style = VoiceEngine.BuildStyle(profile, new EmotionState());

            Assert.Contains("streetwise", style);
            Assert.Contains("rural county drawl", style);
            Assert.DoesNotContain("African-American", style);
            Assert.DoesNotContain("formal professional", style);
        }

        [Fact]
        public void IdentityGenerationDoesNotAssignAccentsFromRace()
        {
            var generator = new IdentityGenerator();
            var ped = new PedObservation
            {
                Fingerprint = "test-korean-civilian",
                ModelName = "a_m_y_korean_01",
                IsMale = true,
                CategoryHint = NpcCategories.Civilian
            };

            NpcProfile profile = generator.Generate(ped, "Little Seoul", "test", "npc_test");

            Assert.Equal(NpcCategories.Civilian, profile.Category);
            Assert.Equal("neutral Los Santos", profile.Speech.Accent);
        }

        [Fact]
        public void OrdinaryCivilianIsNotAssignedAnUnobservedJob()
        {
            var ped = new PedObservation
            {
                Fingerprint = "civilian-no-job-evidence",
                ModelName = "model_12345",
                IsMale = true,
                CategoryHint = NpcCategories.Civilian
            };

            NpcProfile profile = new IdentityGenerator().Generate(ped, "Strawberry", "test", "npc_local");

            Assert.Equal("local resident", profile.Occupation);
            Assert.DoesNotContain("barista", profile.Background);
            Assert.DoesNotContain("construction", profile.Background);
            Assert.Contains("exact occupation is not established", profile.Background);
        }

        [Fact]
        public void OldRandomCivilianJobIsReplacedByGroundedIdentity()
        {
            var profile = new NpcProfile
            {
                Category = NpcCategories.Civilian,
                Occupation = "barista",
                Background = "An ordinary resident. Works as a barista."
            };

            Assert.True(NpcRolePolicy.ReconcileObservedRole(profile, NpcCategories.Civilian));
            Assert.Equal("local resident", profile.Occupation);
            Assert.DoesNotContain("Works as a barista", profile.Background);
            Assert.False(NpcRolePolicy.ReconcileObservedRole(profile, NpcCategories.Civilian));
        }

        [Fact]
        public void GangArchetypeDoesNotInventASpecificGangJob()
        {
            var ped = new PedObservation
            {
                Fingerprint = "gang-model",
                ModelName = "model_98765",
                IsMale = true,
                CategoryHint = NpcCategories.Gang
            };

            NpcProfile profile = new IdentityGenerator().Generate(ped, "Chamberlain Hills", "test", "npc_gang");

            Assert.Equal(NpcCategories.Gang, profile.Category);
            Assert.Equal("gang member", profile.Occupation);
        }

        [Fact]
        public void OldSpecificGangJobIsReplacedWhenGangRoleIsObserved()
        {
            var profile = new NpcProfile
            {
                Category = NpcCategories.Gang,
                Occupation = "street dealer",
                Background = "A local gang member. Works as a street dealer."
            };

            Assert.True(NpcRolePolicy.ReconcileObservedRole(profile, NpcCategories.Gang));
            Assert.Equal("gang member", profile.Occupation);
            Assert.DoesNotContain("street dealer", profile.Background);
        }

        [Fact]
        public void GenderedRoleIsNotInferredForAnOrdinaryDriver()
        {
            var ped = new PedObservation
            {
                Fingerprint = "generic-driver",
                ModelName = "model_67890",
                IsMale = true,
                IsInVehicle = true,
                VehicleSeat = -1,
                CategoryHint = NpcCategories.Driver
            };

            NpcProfile profile = new IdentityGenerator().Generate(ped, "Downtown Los Santos", "test", "npc_driver");

            Assert.Equal("driver", profile.Occupation);
            Assert.DoesNotContain("taxi", profile.Occupation);
            Assert.DoesNotContain("truck", profile.Occupation);
        }

        [Theory]
        [InlineData(2, null)]
        [InlineData(3, null)]
        [InlineData(6, NpcCategories.Police)]
        [InlineData(20, NpcCategories.Paramedic)]
        [InlineData(21, NpcCategories.Firefighter)]
        [InlineData(27, NpcCategories.Police)]
        public void GamePedTypesMapToTheirDocumentedRoles(int pedType, string expectedCategory)
        {
            Assert.Equal(expectedCategory, NpcCategories.FromGamePedType(pedType));
        }

        [Fact]
        public void ObservedPoliceRoleCorrectsStaleCivilianOccupation()
        {
            var profile = new NpcProfile
            {
                Category = NpcCategories.Civilian,
                Occupation = "construction laborer",
                Background = "An ordinary resident."
            };

            Assert.True(NpcRolePolicy.ReconcileObservedRole(profile, NpcCategories.Police));
            Assert.Equal(NpcCategories.Police, profile.Category);
            Assert.Equal("police officer", profile.Occupation);
            Assert.Contains("long shifts", profile.Background);
            Assert.False(NpcRolePolicy.ReconcileObservedRole(profile, NpcCategories.Police));
        }

        private static EngagementState BeginOnFoot(double inactivitySeconds = 240, double leaveRadius = 45)
        {
            var state = new EngagementState(new EngagementSettings
            {
                InactivityTimeoutSeconds = inactivitySeconds,
                LeaveRadiusMeters = leaveRadius
            });
            state.Begin(T0, 4321, "npc_marisol", false, false, 0);
            state.MarkReady(T0);
            return state;
        }

        private static EngagementState BeginInVehicle(bool driver)
        {
            var state = new EngagementState(new EngagementSettings());
            state.Begin(T0, 777, "npc_driver", true, driver, 9001);
            state.MarkReady(T0);
            return state;
        }

        [Fact]
        public void StaysEngagedAfterTheFirstResponse()
        {
            EngagementState state = BeginOnFoot();
            state.NotePlayerSpeech(T0.AddSeconds(2));
            state.NoteNpcResponse(T0.AddSeconds(4));
            state.NoteTurnComplete(T0.AddSeconds(5));

            EngagementDecision decision = state.Evaluate(T0.AddSeconds(6), 2.0, true, true, false);

            Assert.False(decision.Ends);
            Assert.True(state.IsActive);
            Assert.Equal(1, state.NpcTurns);
        }

        [Fact]
        public void StaysEngagedThroughNaturalPauses()
        {
            EngagementState state = BeginOnFoot();
            state.NoteTurnComplete(T0.AddSeconds(5));

            // The player listens, thinks and takes half a minute before replying.
            for (int second = 10; second <= 220; second += 10)
                Assert.False(state.Evaluate(T0.AddSeconds(second), 2.0, true, true, false).Ends);

            Assert.True(state.IsActive);
            Assert.Equal(ConversationEndReason.None, state.EndReason);
        }

        [Fact]
        public void EndsOnlyAfterTheConfiguredInactivityWindow()
        {
            EngagementState state = BeginOnFoot(inactivitySeconds: 240);

            Assert.False(state.Evaluate(T0.AddSeconds(239), 2.0, true, true, false).Ends);

            EngagementDecision decision = state.Evaluate(T0.AddSeconds(241), 2.0, true, true, false);
            Assert.True(decision.Ends);
            Assert.Equal(ConversationEndReason.Inactivity, decision.Reason);
        }

        [Fact]
        public void PlayerSpeechResetsTheInactivityWindow()
        {
            EngagementState state = BeginOnFoot(inactivitySeconds: 60);

            Assert.False(state.Evaluate(T0.AddSeconds(50), 2.0, true, true, false).Ends);
            state.NotePlayerSpeech(T0.AddSeconds(50));
            Assert.False(state.Evaluate(T0.AddSeconds(100), 2.0, true, true, false).Ends);
            Assert.True(state.IsActive);
        }

        [Fact]
        public void FarewellEndsOnlyAfterTheReplyAndItsAudioFinish()
        {
            Assert.False(ConversationLifecyclePolicy.ShouldFinishFarewell(false, true, false));
            Assert.False(ConversationLifecyclePolicy.ShouldFinishFarewell(true, false, false));
            Assert.False(ConversationLifecyclePolicy.ShouldFinishFarewell(true, true, true));
            Assert.True(ConversationLifecyclePolicy.ShouldFinishFarewell(true, true, false));
        }

        [Fact]
        public void HoldsAnOnFootNpcInPlace()
        {
            EngagementState state = BeginOnFoot();

            EngagementDecision first = state.Evaluate(T0, 2.0, true, true, false);
            Assert.False(first.Ends);
            Assert.Equal(EngagementHold.FacePlayer, first.Hold);

            // Attention is refreshed at sub-second cadence without restarting
            // the body turn on every frame.
            Assert.Equal(EngagementHold.None, state.Evaluate(T0.AddMilliseconds(400), 2.0, true, true, false).Hold);
            Assert.Equal(EngagementHold.FacePlayer, state.Evaluate(T0.AddSeconds(1), 2.0, true, true, false).Hold);
        }

        [Fact]
        public void DoesNotWanderOffInsideTheLeaveRadius()
        {
            EngagementState state = BeginOnFoot(leaveRadius: 45);

            // Stepping around the car, or the NPC drifting a few metres, is not
            // a reason to end the conversation.
            Assert.False(state.Evaluate(T0.AddSeconds(10), 12.0, true, true, false).Ends);
            Assert.False(state.Evaluate(T0.AddSeconds(20), 39.0, true, true, false).Ends);
            Assert.True(state.IsActive);
        }

        [Fact]
        public void DriverKeepsTheVehicleForTheWholeConversation()
        {
            EngagementState state = BeginInVehicle(driver: true);

            Assert.True(state.InVehicle);
            Assert.True(state.IsDriver);

            // Seventeen ticks: no face task, so nothing can pull the driver out
            // of the car, and the hold is always the in-vehicle one.
            for (int tick = 0; tick < 17; tick++)
            {
                EngagementDecision decision = state.Evaluate(T0.AddSeconds(tick), 3.0, true, true, false);
                Assert.False(decision.Ends);
                Assert.NotEqual(EngagementHold.FacePlayer, decision.Hold);
            }

            Assert.Equal(EngagementHold.StayInVehicle, state.Evaluate(T0.AddSeconds(20), 3.0, true, true, false).Hold);
            Assert.Equal(9001, state.VehicleHandle);
        }

        [Fact]
        public void PassengerKeepsTheVehicleToo()
        {
            EngagementState state = BeginInVehicle(driver: false);

            Assert.True(state.InVehicle);
            Assert.False(state.IsDriver);
            Assert.Equal(EngagementHold.StayInVehicle, state.Evaluate(T0, 3.0, true, true, false).Hold);
        }

        [Fact]
        public void PlayerKeepsTalkingWithoutStartingAgain()
        {
            EngagementState state = BeginOnFoot();

            // P is pressed once. Five questions later it is still the same
            // engagement, with the same partner.
            for (int turn = 0; turn < 5; turn++)
            {
                DateTime at = T0.AddSeconds(10 + turn * 30);
                state.NotePlayerSpeech(at);
                state.NoteNpcResponse(at.AddSeconds(3));
                state.NoteTurnComplete(at.AddSeconds(5));
                Assert.False(state.Evaluate(at.AddSeconds(6), 2.0, true, true, false).Ends);
                Assert.True(state.IsActive);
            }

            Assert.Equal(5, state.NpcTurns);
            Assert.Equal(4321, state.NpcHandle);
        }

        [Fact]
        public void EndsWhenThePlayerWalksAway()
        {
            EngagementState state = BeginOnFoot(leaveRadius: 45);

            EngagementDecision decision = state.Evaluate(T0.AddSeconds(10), 46.0, true, true, false);

            Assert.True(decision.Ends);
            Assert.Equal(ConversationEndReason.OutOfRange, decision.Reason);
        }

        [Fact]
        public void EmergencyInterruptsTheConversation()
        {
            EngagementState state = BeginOnFoot();

            EngagementDecision decision = state.Evaluate(T0.AddSeconds(4), 2.0, true, true, emergency: true);

            Assert.True(decision.Ends);
            Assert.Equal(ConversationEndReason.Emergency, decision.Reason);
        }

        [Fact]
        public void DeathAndDisappearanceEndTheConversation()
        {
            EngagementState npc = BeginOnFoot();
            Assert.Equal(ConversationEndReason.Unavailable,
                npc.Evaluate(T0.AddSeconds(4), 2.0, npcAlive: false, playerAlive: true, emergency: false).Reason);

            EngagementState player = BeginOnFoot();
            Assert.Equal(ConversationEndReason.PlayerDied,
                player.Evaluate(T0.AddSeconds(4), 2.0, npcAlive: true, playerAlive: false, emergency: false).Reason);
        }

        [Fact]
        public void EndingStopsTheHoldAndRestoresNormalBehaviour()
        {
            EngagementState state = BeginOnFoot();
            Assert.Equal(EngagementHold.FacePlayer, state.Evaluate(T0, 2.0, true, true, false).Hold);

            state.End(T0.AddSeconds(30), ConversationEndReason.PlayerStopped);

            Assert.Equal(EngagementPhase.Ended, state.Phase);
            Assert.False(state.IsActive);
            Assert.Equal(ConversationEndReason.PlayerStopped, state.EndReason);

            // Nothing keeps holding a ped whose conversation is over.
            EngagementDecision after = state.Evaluate(T0.AddSeconds(31), 2.0, true, true, false);
            Assert.False(after.Ends);
            Assert.Equal(EngagementHold.None, after.Hold);
        }

        [Fact]
        public void AnEndedConversationDoesNotEndTwice()
        {
            EngagementState state = BeginOnFoot();
            state.End(T0.AddSeconds(5), ConversationEndReason.PlayerStopped);
            state.End(T0.AddSeconds(6), ConversationEndReason.Emergency);

            Assert.Equal(ConversationEndReason.PlayerStopped, state.EndReason);
        }

        [Fact]
        public void SpokenDialogueNeverAppearsOnScreen()
        {
            Assert.False(HudDialoguePolicy.ShowCaptions);
            Assert.Null(HudDialoguePolicy.Caption("Marisol", "Hey, what's up?"));
            Assert.Null(HudDialoguePolicy.HeardLine("what's the time"));
        }

        [Fact]
        public void LivePersonaRequiresEnglishAndStaysInTheConversation()
        {
            var profile = new NpcProfile
            {
                NpcId = "npc_marisol",
                DisplayName = "Marisol Vega",
                Age = 34,
                Occupation = "barista",
                PersonalityDescription = "Warm but busy, quick to joke with people she likes.",
                Quirks = new List<string> { "repeats the last word of a sentence under their breath" }
            };

            string persona = LivePersona.Build(profile, new EmotionState(), new Relationship(),
                new MemoryItem[0], new ConversationTurn[0], "Strawberry", "clear", 14, false, false);

            Assert.Contains(DialogueLanguage.EnglishRule, persona);
            Assert.Contains("Stay with them", persona);
            Assert.Contains("one brief natural farewell", persona);
            Assert.Contains("If you genuinely want to end the conversation", persona);
            Assert.DoesNotContain("Answer in the language the player speaks", persona);
            Assert.Contains("Marisol Vega", persona);
            Assert.Contains("do not echo or deliberately repeat the last word", persona);

            // Personality is preserved while the language is pinned.
            Assert.Contains("Warm but busy", persona);
        }

        [Fact]
        public void LivePersonaIncludesRoadAndNearbyVehicleAwareness()
        {
            var profile = new NpcProfile { NpcId = "npc_marisol", DisplayName = "Marisol Vega" };

            string persona = LivePersona.Build(profile, new EmotionState(), new Relationship(),
                new MemoryItem[0], new ConversationTurn[0], "Strawberry", "clear", 14, false, false,
                npcOnRoad: true, npcVehiclesNearby: true);

            Assert.Contains("standing on a road", persona);
            Assert.Contains("vehicles are nearby", persona);
            Assert.Contains("Do not claim to have moved", persona);
        }

        [Fact]
        public void TheEnglishRuleForbidsSwitchingLanguages()
        {
            Assert.Contains("Never switch to Spanish", DialogueLanguage.EnglishRule);
            Assert.Contains("conversational American English", DialogueLanguage.EnglishRule);
            Assert.Contains("American English", DialogueLanguage.GroupEnglishRule);
        }
    }
}
