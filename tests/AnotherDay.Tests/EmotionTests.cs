using System;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public class EmotionTests
    {
        private static NpcProfile Profile(float aggression = 0.5f, float suspicion = 0.5f, float confidence = 0.5f)
        {
            return new NpcProfile
            {
                NpcId = "npc_emotion",
                DisplayName = "Tester",
                Category = NpcCategories.Civilian,
                Personality = new PersonalityTraits
                {
                    Aggression = aggression,
                    Suspicion = suspicion,
                    Confidence = confidence,
                    Friendliness = 0.5f,
                    Empathy = 0.5f
                }
            };
        }

        [Fact]
        public void ThreatRaisesAngerAndFearAndDropsTrust()
        {
            var engine = new EmotionEngine();
            var profile = Profile();
            EmotionState emotion = engine.Baseline(profile);
            float angerBefore = emotion.Anger;
            float fearBefore = emotion.Fear;
            float trustBefore = emotion.Trust;

            engine.ApplyPlayerUtterance(emotion, profile, new Relationship { NpcId = profile.NpcId, TargetId = "player" },
                "Say one more word and I will put you on the ground.", -0.8f);

            Assert.True(emotion.Anger > angerBefore);
            Assert.True(emotion.Fear >= fearBefore);
            Assert.True(emotion.Trust < trustBefore);
        }

        [Fact]
        public void ApologyImprovesTrustAndHappiness()
        {
            var engine = new EmotionEngine();
            var profile = Profile();
            EmotionState emotion = engine.Baseline(profile);
            emotion.Trust = 0.3f;
            float happinessBefore = emotion.Happiness;

            engine.ApplyPlayerUtterance(emotion, profile, new Relationship { NpcId = profile.NpcId, TargetId = "player" },
                "I'm sorry, that was my fault. I didn't mean to scare you.", 0.6f);

            Assert.True(emotion.Trust > 0.3f);
            Assert.True(emotion.Happiness >= happinessBefore);
        }

        [Fact]
        public void EmotionsDecayTowardPersonalityBaseline()
        {
            var engine = new EmotionEngine();
            var profile = Profile();
            EmotionState baseline = engine.Baseline(profile);
            var emotion = new EmotionState
            {
                Happiness = 0.05f,
                Anger = 0.9f,
                Fear = 0.9f,
                Stress = 0.9f,
                Sadness = 0.9f,
                Trust = baseline.Trust,
                Confidence = baseline.Confidence,
                Frustration = 0.9f
            };

            engine.Decay(emotion, profile, DateTime.UtcNow.AddHours(6), 0.5);

            Assert.True(emotion.Anger < 0.9f);
            Assert.True(emotion.Stress < 0.9f);
            Assert.True(emotion.Frustration < 0.9f);
        }

        [Fact]
        public void FearfulLowConfidenceNpcPrefersToFlee()
        {
            var engine = new EmotionEngine();
            var timid = Profile(aggression: 0.05f, suspicion: 0.9f, confidence: 0.1f);
            var emotion = new EmotionState { Fear = 0.95f, Stress = 0.9f, Anger = 0.05f, Confidence = 0.05f };

            Assert.True(engine.ShouldFlee(timid, emotion));
        }

        [Fact]
        public void ReactionToGunfireDependsOnDistanceAndPersonality()
        {
            var engine = new EmotionEngine();
            var profile = Profile();
            var close = engine.Baseline(profile);
            var far = engine.Baseline(profile);
            var gunfire = new WorldEventRecord
            {
                EventType = WorldEventTypes.Gunshot,
                Description = "Gunshots nearby",
                Position = new Vec3(),
                Radius = 40f,
                Magnitude = 0.9f
            };

            engine.ApplyWorldEvent(close, profile, gunfire, 5f);
            engine.ApplyWorldEvent(far, profile, gunfire, 120f);

            Assert.True(close.Fear > far.Fear);
            Assert.True(close.Stress > far.Stress);
        }
    }
}
