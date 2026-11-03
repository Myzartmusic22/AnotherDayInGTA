using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Ai
{
    /// <summary>
    /// Local behavior used when Gemini is not configured, offline, rate limited,
    /// or returns an unusable response. It is intentionally simpler than the AI
    /// but keeps personality, mood, relationship and knowledge boundaries intact
    /// so the game remains playable without a network connection.
    /// </summary>
    public sealed class OfflineProvider : IAiProvider
    {
        public bool IsConfigured => true;
        public string Name => "offline";

        public Task<AiConversationResult> ConverseAsync(ConversationContext c, CancellationToken ct)
        {
            var rng = new StableRandom(HashSeed(c.Profile.NpcId + "|" + (c.PlayerText ?? "") + "|" + DateTime.UtcNow.Minute));
            string text = (c.PlayerText ?? "").ToLowerInvariant();
            EmotionEngine emotions = new EmotionEngine();
            float sentiment = emotions.AnalyzeSentiment(c.PlayerText);
            bool threat = ContainsAny(text, "kill", "shoot", "gun", "hurt", "beat", "stab", "die", "weapon");
            bool greeting = ContainsAny(text, "hello", "hey", "hi ", "good morning", "good afternoon", "good evening", "what's up", "whats up", "yo ");
            bool apology = ContainsAny(text, "sorry", "apologize", "apologise", "my bad");
            bool help = ContainsAny(text, "help", "assist", "hand", "favor", "favour");
            bool money = ContainsAny(text, "money", "cash", "dollars", "loan", "spare change");
            bool police = ContainsAny(text, "police", "cop", "cops", "law");
            bool gang = ContainsAny(text, "gang", "ballas", "vagos", "families", "turf");
            bool insult = ContainsAny(text, "idiot", "stupid", "loser", "bitch", "asshole", "shut up", "trash", "pathetic", "screw you");
            bool question = text.Contains("?") || ContainsAny(text, "what", "why", "how", "where", "who", "when", "do you", "did you");
            bool goodbye = ContainsAny(text, "bye", "later", "see you", "leave me", "go away");

            var result = new AiConversationResult { UsedFallback = true };
            var delta = new EmotionDelta();
            var rel = new RelationshipDelta();
            string reply;

            if (threat)
            {
                delta.Fear = 0.2f;
                delta.Anger = 0.12f + c.Profile.Personality.Aggression * 0.15f;
                delta.Trust = -0.12f;
                rel.Trust = -0.12f;
                rel.Fear = 0.15f;
                reply = Pick(rng, "Back up. I don't want any trouble.", "You're not going to threaten me and walk away clean.",
                    "Easy. Put that away and we can talk.", "I'm not looking for a problem. Walk on.");
                if (c.Profile.Personality.Confidence < 0.35f || c.Profile.Category == NpcCategories.Civilian)
                {
                    result.Actions.Add(new AiActionProposal { Type = ActionTypes.Flee, Reason = "Felt threatened by the player.", Priority = 5, DurationMs = 12000 });
                }
                result.Memories.Add(new AiMemoryProposal { Content = "The player threatened me near " + c.Zone + ".", Importance = 0.75f, Kind = "conflict" });
            }
            else if (insult)
            {
                delta.Anger = 0.15f;
                delta.Happiness = -0.06f;
                rel.Affinity = -0.08f;
                rel.Trust = -0.06f;
                reply = c.Profile.Personality.Aggression > 0.6f
                    ? Pick(rng, "Say that again and see what happens.", "You got a lot of mouth for somebody standing this close.", "Keep talking. See where it gets you.")
                    : Pick(rng, "I don't know what your problem is, but I'm not doing this.", "Whatever. I've got somewhere to be.", "You can keep that attitude to yourself.");
                if (c.Profile.Personality.Aggression < 0.5f)
                    result.Actions.Add(new AiActionProposal { Type = ActionTypes.WalkAway, Reason = "Did not want to continue an insulting conversation.", Priority = 3, DurationMs = 6000 });
                result.Memories.Add(new AiMemoryProposal { Content = "The player insulted me.", Importance = 0.55f, Kind = "conflict" });
            }
            else if (money || (help && c.PlayerRelationship != null && c.PlayerRelationship.Trust < 0.2f))
            {
                bool give = c.Profile.Personality.Friendliness > 0.75f && c.PlayerRelationship != null && c.PlayerRelationship.Trust > 0.4f && rng.NextDouble() < 0.35;
                if (give)
                {
                    reply = Pick(rng, "Here. Don't make me regret it.", "This once. I mean it.", "Take a couple dollars. That's all I've got to spare.");
                    delta.Happiness = 0.08f;
                    rel.Trust = 0.1f;
                    rel.Affinity = 0.12f;
                }
                else
                {
                    reply = c.Profile.Category == NpcCategories.Homeless
                        ? Pick(rng, "I've got less than you do, friend.", "Not today. I'm trying to get through the day myself.")
                        : Pick(rng, "I can't help you with that.", "No. I've got my own problems.", "Ask somebody else. I'm not a bank.");
                    delta.Frustration = 0.06f;
                    rel.Trust = -0.02f;
                }
                result.Memories.Add(new AiMemoryProposal { Content = "The player asked me for " + (money ? "money" : "help") + ".", Importance = 0.45f, Kind = "request" });
            }
            else if (police)
            {
                if (c.Profile.Category == NpcCategories.Police)
                {
                    reply = Pick(rng, "Unless you've got a crime to report, keep moving.", "I don't discuss department business on the street.",
                        "If something happened, tell me plainly. Otherwise this conversation's over.");
                    delta.Confidence = 0.06f;
                }
                else
                {
                    reply = Pick(rng, "Cops around here do what they want. I keep my distance.", "You want to talk to the police, that's your business. I stay out of it.",
                        "They come through when it's already too late. That's my experience.");
                    delta.Frustration = 0.04f;
                }
                result.Memories.Add(new AiMemoryProposal { Content = "The player brought up the police.", Importance = 0.4f, Kind = "opinion" });
            }
            else if (gang)
            {
                if (c.Profile.Category == NpcCategories.Gang)
                {
                    reply = c.PlayerRelationship != null && c.PlayerRelationship.Trust > 0.3f
                        ? Pick(rng, "You don't ask about that out loud. Not around here.", "That's not a conversation for the street.")
                        : Pick(rng, "You're asking the wrong questions.", "I don't know you like that.", "Keep walking before somebody thinks you're a problem.");
                    delta.Anger = 0.05f;
                    rel.Trust = -0.05f;
                }
                else
                {
                    reply = Pick(rng, "I mind my own business about that.", "Every block's got its own people. I stay in mine.", "You hear things, but you don't repeat them.");
                }
                result.Memories.Add(new AiMemoryProposal { Content = "The player asked about gangs.", Importance = 0.5f, Kind = "opinion" });
            }
            else if (apology)
            {
                delta.Anger = -0.1f;
                delta.Trust = 0.05f;
                rel.Trust = 0.05f;
                reply = Pick(rng, "Alright. We're square.", "Appreciate that. Most people wouldn't say it.", "Fine. Just don't let it happen again.");
                result.Memories.Add(new AiMemoryProposal { Content = "The player apologized to me.", Importance = 0.5f, Kind = "relationship" });
            }
            else if (greeting)
            {
                if (c.PlayerRelationship != null && c.PlayerRelationship.Familiarity > 0.3f)
                    reply = Pick(rng, "Hey. You're still around, huh?", "Back again. What do you need?", "Well, look who it is.");
                else if (c.Profile.Personality.Friendliness > 0.6f)
                    reply = Pick(rng, "Hey there. You alright?", "Afternoon. Something I can do for you?", "What's going on?");
                else if (c.Profile.Personality.Suspicion > 0.65f)
                    reply = Pick(rng, "Yeah. What do you want?", "You need something?", "I don't know you. Something I can help with?");
                else
                    reply = Pick(rng, "Hey.", "What's up.", "Morning.");
                delta.Trust = 0.02f;
                result.Actions.Add(new AiActionProposal { Type = ActionTypes.Face, Reason = "Acknowledged the player.", Priority = 2, DurationMs = 3000 });
                result.Memories.Add(new AiMemoryProposal { Content = "The player greeted me near " + c.Zone + ".", Importance = 0.35f, Kind = "conversation" });
            }
            else if (question)
            {
                if (c.Profile.Category == NpcCategories.Police)
                    reply = Pick(rng, "I can't help you with that.", "Ask me something I can actually answer.", "That's not something I'm going to discuss.");
                else if (c.Profile.Personality.Humor > 0.65f)
                    reply = Pick(rng, "You're asking me? I barely know what I'm doing today.", "If I knew that, I'd be charging for it.", "Depends who's asking, and why.");
                else if (c.Profile.Personality.Intelligence > 0.65f)
                    reply = Pick(rng, "I've got my own read on it, but I wasn't there.", "I don't know enough to say. I won't make something up.", "What I've heard doesn't add up. That's all I'll say.");
                else
                    reply = Pick(rng, "Couldn't tell you.", "I don't know. I try not to get mixed up in it.", "Your guess is as good as mine.");
                result.Memories.Add(new AiMemoryProposal { Content = "The player asked me a question I could not fully answer.", Importance = 0.3f, Kind = "conversation" });
            }
            else if (goodbye)
            {
                reply = Pick(rng, "Yeah. Take care.", "Later.", "Watch yourself out there.");
                result.WantsToEnd = true;
            }
            else
            {
                if (c.Profile.Personality.Sociability > 0.65f)
                    reply = Pick(rng, "I hear you. Things have been strange around here lately.", "That's the thing about this city. Never a quiet day.", "You know, most people don't stop to talk. It's kind of nice.");
                else if (c.Profile.Personality.Friendliness < 0.35f)
                    reply = Pick(rng, "If you say so.", "Mm-hm.", "Is there a point to this?");
                else
                    reply = Pick(rng, "Yeah, I get that.", "Fair enough.", "That's how it goes sometimes.");
                delta.Trust = 0.01f;
            }

            if (sentiment > 0.15f) { delta.Happiness = 0.05f; rel.Affinity = 0.04f; }
            if (sentiment < -0.15f) { delta.Anger = 0.05f; rel.Affinity = -0.05f; }
            if (c.PlayerArmed) { delta.Fear += 0.05f; delta.Trust -= 0.05f; }
            if (c.PlayerAiming) { delta.Fear += 0.12f; rel.Fear = 0.1f; }

            result.Dialogue = reply;
            result.EmotionDelta = delta;
            result.RelationshipDelta = rel;
            result.InnerThought = "Local behavior: " + (threat ? "threatened" : greeting ? "greeted" : "conversation continues") + ".";
            if (c.Profile.Personality.Sociability < 0.25f && rng.NextDouble() < 0.25)
                result.WantsToEnd = true;
            return Task.FromResult(result);
        }

        public Task<AiGroupResult> ConverseGroupAsync(GroupContext c, CancellationToken ct)
        {
            var result = new AiGroupResult { UsedFallback = true };
            if (c.Participants == null || c.Participants.Count < 2) return Task.FromResult(result);

            var rng = new StableRandom(HashSeed(string.Join("|", c.Participants.Select(p => p.NpcId)) + c.Topic + DateTime.UtcNow.Minute));
            int turns = rng.Next(2, 5);
            var openers = new[]
            {
                "You hear about what happened over on the next block?",
                "This weather's got everybody acting strange.",
                "I'm telling you, the city's getting worse.",
                "You working today, or what?",
                "Somebody was shouting down the street a minute ago.",
                "You seen the price of everything lately?"
            };
            var replies = new[]
            {
                "I don't know. I keep my head down.",
                "Yeah, I heard something. I'm not repeating it.",
                "Could be nothing. People talk.",
                "That's the way it's always been around here.",
                "You worry too much.",
                "I've got enough problems without borrowing yours.",
                "Maybe. Doesn't change anything for me.",
                "If it's true, somebody's going to pay for it."
            };

            var first = c.Participants[0];
            var second = c.Participants[1];
            result.Turns.Add(new AiGroupTurn { SpeakerNpcId = first.NpcId, Text = c.Topic ?? openers[rng.Next(openers.Length)] });
            result.Turns.Add(new AiGroupTurn
            {
                SpeakerNpcId = second.NpcId,
                Text = replies[rng.Next(replies.Length)],
                EmotionDelta = new EmotionDelta { Trust = 0.02f }
            });
            for (int i = 2; i < turns; i++)
            {
                NpcProfile speaker = i % 2 == 0 ? first : second;
                result.Turns.Add(new AiGroupTurn
                {
                    SpeakerNpcId = speaker.NpcId,
                    Text = i % 3 == 0 ? Pick(rng, "Anyway. I've got to get going.", "Yeah. See you around.", "Alright, take it easy.") : replies[rng.Next(replies.Length)]
                });
            }

            result.Memories.Add(new AiMemoryProposal
            {
                Content = "I talked with " + second.DisplayName + " near " + (c.Zone ?? "the street") + " about " + (c.Topic ?? "the neighborhood") + ".",
                Importance = 0.3f,
                Kind = "social",
                SubjectNpcId = second.NpcId
            });
            return Task.FromResult(result);
        }

        private static string Pick(StableRandom rng, params string[] options) => options[rng.Next(options.Length)];

        private static bool ContainsAny(string text, params string[] words)
        {
            foreach (string word in words)
                if (text.Contains(word)) return true;
            return false;
        }

        private static ulong HashSeed(string value)
        {
            ulong hash = 1469598103934665603UL;
            foreach (char c in value ?? "")
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
