using System;
using System.Collections.Generic;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    /// <summary>
    /// Local, rule-based emotion updates. Used for immediate gameplay reactions
    /// without spending a Gemini request, and as a clamp on AI-proposed deltas.
    /// </summary>
    public sealed class EmotionEngine
    {
        private static readonly HashSet<string> PositiveWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "thanks", "thank", "please", "help", "friend", "nice", "good", "great", "cool", "love",
            "appreciate", "sorry", "kind", "beautiful", "fun", "happy", "welcome", "respect", "solid"
        };

        private static readonly HashSet<string> NegativeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "idiot", "stupid", "shut", "hate", "ugly", "trash", "pathetic", "loser", "kill", "hurt",
            "gun", "shoot", "threat", "bitch", "asshole", "fuck", "damn", "suck", "boring", "liar",
            "steal", "rob", "pig", "cop", "gang"
        };

        private static readonly HashSet<string> ThreatWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kill", "shoot", "gun", "hurt", "beat", "stab", "destroy", "burn", "die", "weapon", "threat"
        };

        private static readonly HashSet<string> ApologyWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sorry", "apologize", "apologise", "my bad", "didn't mean", "didnt mean"
        };

        public EmotionState Baseline(NpcProfile profile)
        {
            var emotion = new EmotionState
            {
                Happiness = 0.45f + (profile.Personality.Friendliness - 0.5f) * 0.3f,
                Anger = 0.05f + profile.Personality.Aggression * 0.12f,
                Fear = 0.05f + profile.Personality.Suspicion * 0.08f,
                Stress = 0.15f + (1f - profile.Personality.Confidence) * 0.15f,
                Sadness = 0.08f,
                Trust = 0.35f + profile.Personality.Empathy * 0.15f,
                Confidence = 0.45f + profile.Personality.Confidence * 0.2f,
                Frustration = 0.05f
            };
            emotion.Clamp();
            return emotion;
        }

        public void Decay(EmotionState emotion, NpcProfile profile, DateTime nowUtc, double perHour)
        {
            double hours = Math.Max(0, (nowUtc - emotion.UpdatedUtc).TotalHours);
            if (hours <= 0.01 || hours > 24 * 30) hours = Math.Min(hours, 24);
            double rate = Math.Clamp(perHour * hours, 0, 0.65);
            EmotionState baseline = Baseline(profile);
            emotion.Happiness = Move(emotion.Happiness, baseline.Happiness, rate);
            emotion.Anger = Move(emotion.Anger, baseline.Anger, rate);
            emotion.Fear = Move(emotion.Fear, baseline.Fear, rate);
            emotion.Stress = Move(emotion.Stress, baseline.Stress, rate);
            emotion.Sadness = Move(emotion.Sadness, baseline.Sadness, rate);
            emotion.Trust = Move(emotion.Trust, baseline.Trust, rate * 0.5f);
            emotion.Confidence = Move(emotion.Confidence, baseline.Confidence, rate);
            emotion.Frustration = Move(emotion.Frustration, baseline.Frustration, rate);
            emotion.UpdatedUtc = nowUtc;
            emotion.Clamp();
        }

        public void ApplyWorldEvent(EmotionState emotion, NpcProfile profile, WorldEventRecord evt, double distanceMeters)
        {
            if (evt == null) return;
            float proximity = (float)Math.Clamp(1.0 - (distanceMeters / Math.Max(8, evt.Radius * 2.5)), 0.1, 1.0);
            float personalityFear = 0.6f + profile.Personality.Suspicion * 0.6f - profile.Personality.Confidence * 0.3f;
            float empathy = profile.Personality.Empathy;
            string type = evt.EventType ?? "";

            switch (type)
            {
                case WorldEventTypes.Gunshot:
                    emotion.Fear += 0.18f * proximity * personalityFear;
                    emotion.Stress += 0.12f * proximity;
                    if (profile.Category == NpcCategories.Police) emotion.Confidence += 0.06f * proximity;
                    break;
                case WorldEventTypes.Explosion:
                    emotion.Fear += 0.32f * proximity * personalityFear;
                    emotion.Stress += 0.2f * proximity;
                    emotion.Confidence -= 0.08f * proximity;
                    break;
                case WorldEventTypes.Fight:
                    emotion.Anger += 0.08f * proximity * profile.Personality.Aggression;
                    emotion.Fear += 0.1f * proximity;
                    if (profile.Category == NpcCategories.Police) emotion.Confidence += 0.05f;
                    break;
                case WorldEventTypes.PoliceActivity:
                    if (profile.Category == NpcCategories.Police) { emotion.Confidence += 0.1f; emotion.Stress += 0.05f; }
                    else if (profile.Category == NpcCategories.Gang) { emotion.Fear += 0.12f; emotion.Anger += 0.08f; }
                    else { emotion.Stress += 0.08f * proximity; }
                    break;
                case WorldEventTypes.Injury:
                case WorldEventTypes.Death:
                    emotion.Sadness += 0.12f * proximity * empathy;
                    emotion.Fear += 0.08f * proximity;
                    emotion.Stress += 0.08f * proximity;
                    break;
                case WorldEventTypes.Threat:
                case WorldEventTypes.PlayerAiming:
                    emotion.Fear += 0.2f * proximity * personalityFear;
                    emotion.Anger += 0.12f * proximity * profile.Personality.Aggression;
                    emotion.Trust -= 0.1f * proximity;
                    break;
                case WorldEventTypes.Crash:
                    emotion.Stress += 0.07f * proximity;
                    emotion.Fear += 0.05f * proximity;
                    break;
                case WorldEventTypes.Robbery:
                    emotion.Fear += 0.14f * proximity;
                    emotion.Anger += 0.1f * proximity;
                    break;
                case WorldEventTypes.EmergencyVehicle:
                    if (profile.Category == NpcCategories.Paramedic || profile.Category == NpcCategories.Firefighter)
                        emotion.Confidence += 0.08f;
                    else emotion.Stress += 0.05f * proximity;
                    break;
                case WorldEventTypes.WeatherChange:
                    emotion.Happiness -= 0.03f;
                    break;
            }

            emotion.UpdatedUtc = DateTime.UtcNow;
            emotion.Clamp();
        }

        public float AnalyzeSentiment(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string[] words = text.ToLowerInvariant().Split(new[] { ' ', '.', ',', '!', '?', ';', ':', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries);
            int positive = 0, negative = 0, threats = 0;
            foreach (string word in words)
            {
                if (PositiveWords.Contains(word)) positive++;
                if (NegativeWords.Contains(word)) negative++;
                if (ThreatWords.Contains(word)) threats++;
            }
            if (text.IndexOf("thank you", StringComparison.OrdinalIgnoreCase) >= 0) positive += 2;
            if (text.IndexOf("screw you", StringComparison.OrdinalIgnoreCase) >= 0) negative += 2;
            float score = (positive - negative) / (float)Math.Max(3, positive + negative + 1);
            if (threats > 0) score -= 0.3f * Math.Min(1f, threats * 0.5f);
            return Math.Clamp(score, -1f, 1f);
        }

        public bool IsApology(string text) =>
            !string.IsNullOrEmpty(text) && ContainsAny(text.ToLowerInvariant(), ApologyWords);

        public bool IsThreat(string text) =>
            !string.IsNullOrEmpty(text) && ContainsAny(text.ToLowerInvariant(), ThreatWords);

        public void ApplyPlayerUtterance(EmotionState emotion, NpcProfile profile, Relationship relationship, string text, float sentiment)
        {
            float trustFactor = 1f - Math.Clamp((relationship.Trust + 1f) / 2f, 0f, 1f);
            if (sentiment > 0.1f)
            {
                emotion.Happiness += 0.08f * sentiment;
                emotion.Anger -= 0.06f;
                emotion.Trust += 0.04f * sentiment * (0.6f + profile.Personality.Empathy);
                relationship.Trust += 0.03f * sentiment;
                relationship.Affinity += 0.04f * sentiment;
            }
            else if (sentiment < -0.1f)
            {
                emotion.Anger += 0.12f * -sentiment * (0.6f + profile.Personality.Aggression);
                emotion.Happiness -= 0.06f * -sentiment;
                emotion.Trust -= 0.05f * -sentiment * trustFactor;
                emotion.Frustration += 0.08f * -sentiment;
                relationship.Trust += 0.05f * sentiment;
                relationship.Affinity += 0.07f * sentiment;
            }

            if (IsThreat(text))
            {
                emotion.Fear += 0.18f;
                emotion.Anger += 0.15f * profile.Personality.Aggression;
                relationship.Fear += 0.15f;
                relationship.Trust -= 0.12f;
            }
            if (IsApology(text))
            {
                emotion.Anger -= 0.1f;
                relationship.Trust += 0.04f;
            }

            relationship.Familiarity = Math.Clamp(relationship.Familiarity + 0.03f, 0f, 1f);
            relationship.LastInteractionUtc = DateTime.UtcNow;
            relationship.Clamp();
            emotion.UpdatedUtc = DateTime.UtcNow;
            emotion.Clamp();
        }

        public void ApplyAiDeltas(EmotionState emotion, EmotionDelta delta)
        {
            if (delta == null) return;
            emotion.Happiness += ClampDelta(delta.Happiness);
            emotion.Anger += ClampDelta(delta.Anger);
            emotion.Fear += ClampDelta(delta.Fear);
            emotion.Stress += ClampDelta(delta.Stress);
            emotion.Sadness += ClampDelta(delta.Sadness);
            emotion.Trust += ClampDelta(delta.Trust);
            emotion.Confidence += ClampDelta(delta.Confidence);
            emotion.Frustration += ClampDelta(delta.Frustration);
            emotion.UpdatedUtc = DateTime.UtcNow;
            emotion.Clamp();
        }

        public float WillingnessToTalk(NpcProfile profile, EmotionState emotion, Relationship relationship)
        {
            float value = 0.35f + profile.Personality.Sociability * 0.35f + profile.Personality.Friendliness * 0.2f
                          + relationship.Affinity * 0.2f - emotion.Fear * 0.35f - emotion.Anger * 0.25f
                          - emotion.Stress * 0.1f;
            if (profile.Category == NpcCategories.Police) value += 0.1f;
            if (profile.Category == NpcCategories.Gang) value -= relationship.Trust < 0 ? 0.15f : 0f;
            return Math.Clamp(value, 0f, 1f);
        }

        public bool ShouldFlee(NpcProfile profile, EmotionState emotion)
        {
            return emotion.Fear > 0.55f && profile.Personality.Confidence < 0.65f;
        }

        public bool ShouldConfront(NpcProfile profile, EmotionState emotion)
        {
            return emotion.Anger > 0.55f && profile.Personality.Aggression > 0.55f && emotion.Fear < 0.6f;
        }

        private static float ClampDelta(float value) => Math.Clamp(value, -0.25f, 0.25f);
        private static float Move(float value, float target, double rate) => (float)(value + (target - value) * rate);

        private static bool ContainsAny(string text, HashSet<string> set)
        {
            foreach (string word in text.Split(new[] { ' ', '.', ',', '!', '?', ';', ':', '\'' }, StringSplitOptions.RemoveEmptyEntries))
                if (set.Contains(word)) return true;
            foreach (string phrase in set)
                if (phrase.Contains(" ") && text.Contains(phrase)) return true;
            return false;
        }
    }

    public sealed class EmotionDelta
    {
        public float Happiness { get; set; }
        public float Anger { get; set; }
        public float Fear { get; set; }
        public float Stress { get; set; }
        public float Sadness { get; set; }
        public float Trust { get; set; }
        public float Confidence { get; set; }
        public float Frustration { get; set; }
    }
}
