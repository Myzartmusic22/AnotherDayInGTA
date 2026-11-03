using System;
using System.Collections.Generic;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    public sealed class PersonalityTraits
    {
        public float Friendliness { get; set; } = 0.5f;
        public float Aggression { get; set; } = 0.5f;
        public float Confidence { get; set; } = 0.5f;
        public float Intelligence { get; set; } = 0.5f;
        public float Suspicion { get; set; } = 0.5f;
        public float Humor { get; set; } = 0.5f;
        public float Empathy { get; set; } = 0.5f;
        public float Impulsiveness { get; set; } = 0.5f;
        public float Sociability { get; set; } = 0.5f;
        public float Loyalty { get; set; } = 0.5f;

        public float Get(string name)
        {
            switch (name)
            {
                case nameof(Friendliness): return Friendliness;
                case nameof(Aggression): return Aggression;
                case nameof(Confidence): return Confidence;
                case nameof(Intelligence): return Intelligence;
                case nameof(Suspicion): return Suspicion;
                case nameof(Humor): return Humor;
                case nameof(Empathy): return Empathy;
                case nameof(Impulsiveness): return Impulsiveness;
                case nameof(Sociability): return Sociability;
                case nameof(Loyalty): return Loyalty;
                default: return 0.5f;
            }
        }

        public void Set(string name, float value)
        {
            value = Math.Clamp(value, 0f, 1f);
            switch (name)
            {
                case nameof(Friendliness): Friendliness = value; break;
                case nameof(Aggression): Aggression = value; break;
                case nameof(Confidence): Confidence = value; break;
                case nameof(Intelligence): Intelligence = value; break;
                case nameof(Suspicion): Suspicion = value; break;
                case nameof(Humor): Humor = value; break;
                case nameof(Empathy): Empathy = value; break;
                case nameof(Impulsiveness): Impulsiveness = value; break;
                case nameof(Sociability): Sociability = value; break;
                case nameof(Loyalty): Loyalty = value; break;
            }
        }

        public string ShortDescription()
        {
            var parts = new List<string>();
            if (Friendliness > 0.65f) parts.Add("friendly"); else if (Friendliness < 0.3f) parts.Add("cold");
            if (Aggression > 0.7f) parts.Add("quick to anger"); else if (Aggression < 0.25f) parts.Add("non-confrontational");
            if (Confidence > 0.7f) parts.Add("self-assured"); else if (Confidence < 0.3f) parts.Add("insecure");
            if (Suspicion > 0.7f) parts.Add("wary");
            if (Humor > 0.7f) parts.Add("jokes easily");
            if (Empathy > 0.7f) parts.Add("empathetic");
            if (Impulsiveness > 0.7f) parts.Add("impulsive");
            if (Sociability > 0.7f) parts.Add("talkative"); else if (Sociability < 0.3f) parts.Add("withdrawn");
            if (Loyalty > 0.7f) parts.Add("loyal");
            return parts.Count == 0 ? "balanced and guarded" : string.Join(", ", parts);
        }
    }

    public sealed class SpeechStyle
    {
        public string Accent { get; set; } = "neutral Los Santos";
        public string Pace { get; set; } = "normal";
        public string Pitch { get; set; } = "medium";
        public string Vocabulary { get; set; } = "everyday";
        public string Profanity { get; set; } = "occasional";
        public string VerbalTic { get; set; } = "";
        public string SentenceLength { get; set; } = "short";
    }

    public sealed class VoiceAssignment
    {
        public string Provider { get; set; } = "gemini";
        public string VoiceId { get; set; }
        public string VoiceName { get; set; }
        public string Description { get; set; }
        public string Accent { get; set; }
        public string Style { get; set; }
        public bool IsFallback { get; set; }
        public string AssignedUtc { get; set; }
        public string Reason { get; set; }

        /// <summary>Sex of the character this voice was assigned to, recorded at
        /// assignment time so a mismatch can be detected later.</summary>
        public bool IsMale { get; set; }

        /// <summary>Version of the gender table used when this was assigned.</summary>
        public int CatalogVersion { get; set; }
    }

    public sealed class NpcProfile
    {
        public string NpcId { get; set; }
        public string Fingerprint { get; set; }
        public int ModelHash { get; set; }
        public string ModelName { get; set; }
        public bool IsMale { get; set; }
        public string DisplayName { get; set; }
        public int Age { get; set; }
        public string Occupation { get; set; }
        public string Category { get; set; } = NpcCategories.Civilian;
        public string Neighborhood { get; set; }
        public string Background { get; set; }
        public string PersonalityDescription { get; set; }
        public PersonalityTraits Personality { get; set; } = new PersonalityTraits();
        public SpeechStyle Speech { get; set; } = new SpeechStyle();
        public List<string> Interests { get; set; } = new List<string>();
        public List<string> Beliefs { get; set; } = new List<string>();
        public List<string> Quirks { get; set; } = new List<string>();
        public VoiceAssignment Voice { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public Vec3 LastKnownPosition { get; set; } = new Vec3();
        public int TimesSeen { get; set; } = 1;
        public double IdentityConfidence { get; set; } = 1.0;
        public bool IsDormant { get; set; }
        public string GameVersion { get; set; }
    }

    public sealed class EmotionState
    {
        public float Happiness { get; set; } = 0.5f;
        public float Anger { get; set; } = 0.05f;
        public float Fear { get; set; } = 0.05f;
        public float Stress { get; set; } = 0.2f;
        public float Sadness { get; set; } = 0.1f;
        public float Trust { get; set; } = 0.4f;
        public float Confidence { get; set; } = 0.5f;
        public float Frustration { get; set; } = 0.05f;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public void Clamp()
        {
            Happiness = Math.Clamp(Happiness, 0f, 1f);
            Anger = Math.Clamp(Anger, 0f, 1f);
            Fear = Math.Clamp(Fear, 0f, 1f);
            Stress = Math.Clamp(Stress, 0f, 1f);
            Sadness = Math.Clamp(Sadness, 0f, 1f);
            Trust = Math.Clamp(Trust, 0f, 1f);
            Confidence = Math.Clamp(Confidence, 0f, 1f);
            Frustration = Math.Clamp(Frustration, 0f, 1f);
        }

        public string Summary()
        {
            var parts = new List<string>();
            if (Happiness > 0.65f) parts.Add("happy"); else if (Happiness < 0.3f) parts.Add("unhappy");
            if (Anger > 0.55f) parts.Add("angry");
            if (Fear > 0.5f) parts.Add("afraid");
            if (Stress > 0.65f) parts.Add("stressed");
            if (Sadness > 0.55f) parts.Add("sad");
            if (Trust > 0.65f) parts.Add("trusting"); else if (Trust < 0.3f) parts.Add("distrustful");
            if (Confidence > 0.7f) parts.Add("confident"); else if (Confidence < 0.3f) parts.Add("uncertain");
            if (Frustration > 0.6f) parts.Add("frustrated");
            return parts.Count == 0 ? "neutral" : string.Join(", ", parts);
        }

        public string ToneInstruction()
        {
            if (Anger > 0.6f) return "speak with visible irritation, clipped and confrontational";
            if (Fear > 0.6f) return "sound nervous and eager to get away";
            if (Sadness > 0.6f) return "sound subdued and tired";
            if (Happiness > 0.7f) return "sound upbeat and relaxed";
            if (Stress > 0.7f) return "sound hurried and tense";
            if (Trust < 0.25f) return "sound guarded and suspicious";
            return "sound natural and conversational";
        }
    }

    public sealed class Relationship
    {
        public string NpcId { get; set; }
        public string TargetId { get; set; }
        public float Trust { get; set; }
        public float Affinity { get; set; }
        public float Fear { get; set; }
        public float Respect { get; set; }
        public float Familiarity { get; set; }
        public string Status { get; set; } = "stranger";
        public string Notes { get; set; } = "";
        public DateTime LastInteractionUtc { get; set; } = DateTime.UtcNow;

        public void Clamp()
        {
            Trust = Math.Clamp(Trust, -1f, 1f);
            Affinity = Math.Clamp(Affinity, -1f, 1f);
            Fear = Math.Clamp(Fear, 0f, 1f);
            Respect = Math.Clamp(Respect, -1f, 1f);
            Familiarity = Math.Clamp(Familiarity, 0f, 1f);
        }

        public string Describe()
        {
            string label;
            if (Fear > 0.6f) label = "afraid of you";
            else if (Affinity > 0.6f && Trust > 0.5f) label = "friendly toward you";
            else if (Affinity < -0.5f) label = "hostile toward you";
            else if (Trust < -0.3f) label = "distrustful of you";
            else if (Familiarity > 0.5f) label = "acquainted with you";
            else label = "wary of you";
            return string.Format("{0}; trust {1:0.0}, affinity {2:0.0}, fear {3:0.0}, familiarity {4:0.0}", label, Trust, Affinity, Fear, Familiarity);
        }
    }

    public sealed class MemoryItem
    {
        public long Id { get; set; }
        public string NpcId { get; set; }
        public string Kind { get; set; } = "conversation";
        public string Content { get; set; }
        public float Importance { get; set; } = 0.4f;
        public float Sentiment { get; set; }
        public string Keywords { get; set; } = "";
        public string SubjectNpcId { get; set; }
        public string ConversationId { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
        public int AccessCount { get; set; }
    }

    public sealed class ConversationTurn
    {
        public long Id { get; set; }
        public string ConversationId { get; set; }
        public string NpcId { get; set; }
        public string Speaker { get; set; }
        public string Text { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class WorldEventRecord
    {
        public long Id { get; set; }
        public string EventType { get; set; }
        public Vec3 Position { get; set; }
        public float Radius { get; set; }
        public float Magnitude { get; set; }
        public string Description { get; set; }
        public string Zone { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class VoiceOption
    {
        public string VoiceId { get; set; }
        public string Name { get; set; }
        public string Gender { get; set; }
        public string Description { get; set; }
        public string Pitch { get; set; }
        public string Accent { get; set; }
        public string Persona { get; set; }
        public float Energy { get; set; }
        public bool IsFallback { get; set; }
    }

    public sealed class IdentityCandidate
    {
        public NpcProfile Profile { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; }
    }

    public sealed class SocialConversation
    {
        public string ConversationId { get; set; }
        public List<string> NpcIds { get; set; } = new List<string>();
        public Dictionary<string, int> Handles { get; set; } = new Dictionary<string, int>();
        public string Topic { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime EndsUtc { get; set; }
        public List<ConversationTurn> Turns { get; set; } = new List<ConversationTurn>();
        public bool IncludesPlayer { get; set; }
    }
}
