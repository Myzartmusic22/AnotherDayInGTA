using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Domain;

namespace AnotherDay.Service.Ai
{
    public sealed class AiMemoryProposal
    {
        public string Content { get; set; }
        public float Importance { get; set; } = 0.4f;
        public string Kind { get; set; } = "conversation";
        public string SubjectNpcId { get; set; }
        public float Sentiment { get; set; }
    }

    public sealed class AiActionProposal
    {
        public string Type { get; set; }
        public string Target { get; set; }
        public string Reason { get; set; }
        public int Priority { get; set; } = 3;
        public int DurationMs { get; set; } = 4000;
    }

    public sealed class AiConversationResult
    {
        public string Dialogue { get; set; }
        public string InnerThought { get; set; }
        public bool WantsToEnd { get; set; }
        public EmotionDelta EmotionDelta { get; set; } = new EmotionDelta();
        public RelationshipDelta RelationshipDelta { get; set; } = new RelationshipDelta();
        public List<AiMemoryProposal> Memories { get; set; } = new List<AiMemoryProposal>();
        public List<AiActionProposal> Actions { get; set; } = new List<AiActionProposal>();
        public bool UsedFallback { get; set; }
        public string Raw { get; set; }
        public long EstimatedTokens { get; set; }
    }

    public sealed class AiGroupTurn
    {
        public string SpeakerNpcId { get; set; }
        public string Text { get; set; }
        public string InnerThought { get; set; }
        public EmotionDelta EmotionDelta { get; set; } = new EmotionDelta();
    }

    public sealed class AiGroupResult
    {
        public List<AiGroupTurn> Turns { get; set; } = new List<AiGroupTurn>();
        public List<AiMemoryProposal> Memories { get; set; } = new List<AiMemoryProposal>();
        public bool UsedFallback { get; set; }
        public long EstimatedTokens { get; set; }
        public string Raw { get; set; }
    }

    public sealed class RelationshipDelta
    {
        public float Trust { get; set; }
        public float Affinity { get; set; }
        public float Fear { get; set; }
        public float Respect { get; set; }
    }

    public sealed class ConversationContext
    {
        public NpcProfile Profile { get; set; }
        public EmotionState Emotion { get; set; }
        public Relationship PlayerRelationship { get; set; }
        public List<MemoryItem> Memories { get; set; } = new List<MemoryItem>();
        public List<ConversationTurn> RecentTurns { get; set; } = new List<ConversationTurn>();
        public List<NearbyNpc> Nearby { get; set; } = new List<NearbyNpc>();
        public List<WorldEventRecord> RecentEvents { get; set; } = new List<WorldEventRecord>();
        public string PlayerText { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
        public bool PlayerArmed { get; set; }
        public bool PlayerAiming { get; set; }
        public string ConversationId { get; set; }
        public string RelationshipSummary { get; set; }
    }

    public sealed class GroupContext
    {
        public List<NpcProfile> Participants { get; set; } = new List<NpcProfile>();
        public List<EmotionState> Emotions { get; set; } = new List<EmotionState>();
        public List<MemoryItem> Memories { get; set; } = new List<MemoryItem>();
        public List<ConversationTurn> RecentTurns { get; set; } = new List<ConversationTurn>();
        public List<NearbyNpc> Bystanders { get; set; } = new List<NearbyNpc>();
        public List<WorldEventRecord> RecentEvents { get; set; } = new List<WorldEventRecord>();
        public Dictionary<string, Relationship> Relationships { get; set; } = new Dictionary<string, Relationship>();
        public string Topic { get; set; }
        public string Zone { get; set; }
        public string Weather { get; set; }
        public int ClockHour { get; set; }
    }

    public sealed class NearbyNpc
    {
        public string NpcId { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public float Distance { get; set; }
        public string RelationshipSummary { get; set; }
    }

    public interface IAiProvider
    {
        bool IsConfigured { get; }
        string Name { get; }
        Task<AiConversationResult> ConverseAsync(ConversationContext context, CancellationToken ct);
        Task<AiGroupResult> ConverseGroupAsync(GroupContext context, CancellationToken ct);
    }

    public interface IVoiceProvider
    {
        bool IsConfigured { get; }
        Task<byte[]> SynthesizeAsync(string text, string voiceId, string style, CancellationToken ct);
        Task<string> TranscribeAsync(string wavPath, CancellationToken ct);
    }

    public sealed class AiUnavailableException : Exception
    {
        public AiUnavailableException(string message) : base(message) { }
        public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
    }
}
