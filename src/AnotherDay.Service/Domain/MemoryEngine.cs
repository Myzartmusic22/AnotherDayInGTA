using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AnotherDay.Service.Data;

namespace AnotherDay.Service.Domain
{
    /// <summary>
    /// Memory is never dumped wholesale into a prompt. Only the highest scoring
    /// memories for the current situation are retrieved.
    /// </summary>
    public sealed class MemoryEngine
    {
        private readonly Database _db;
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "if", "then", "so", "of", "to", "in", "on", "for",
            "with", "at", "by", "from", "up", "about", "into", "over", "after", "is", "are", "was",
            "were", "be", "been", "being", "have", "has", "had", "do", "does", "did", "will", "would",
            "can", "could", "should", "i", "you", "he", "she", "it", "we", "they", "me", "him", "her",
            "us", "them", "my", "your", "his", "its", "our", "their", "this", "that", "these", "those",
            "what", "which", "who", "when", "where", "why", "how", "not", "no", "yes", "just", "very"
        };

        public MemoryEngine(Database db)
        {
            _db = db;
        }

        public async Task<MemoryItem> RecordInteractionAsync(NpcProfile profile, string playerText, string npcReply,
            float importance, float sentiment, string conversationId)
        {
            string content = BuildInteractionSummary(playerText, npcReply);
            MemoryItem duplicate = await _db.FindRecentDuplicateAsync(profile.NpcId, content, DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
            if (duplicate != null)
            {
                duplicate.Importance = Math.Min(1f, Math.Max(duplicate.Importance, importance) + 0.05f);
                return duplicate;
            }

            var memory = new MemoryItem
            {
                NpcId = profile.NpcId,
                Kind = "conversation",
                Content = content,
                Importance = Math.Clamp(importance, 0.05f, 1f),
                Sentiment = Math.Clamp(sentiment, -1f, 1f),
                Keywords = ExtractKeywords(playerText + " " + npcReply),
                ConversationId = conversationId,
                CreatedUtc = DateTime.UtcNow,
                LastAccessedUtc = DateTime.UtcNow
            };
            memory.Id = await _db.AddMemoryAsync(memory).ConfigureAwait(false);
            return memory;
        }

        public async Task AddMemoryAsync(NpcProfile profile, string kind, string content, float importance, float sentiment,
            string subjectNpcId, string conversationId)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            content = Sanitize(content);
            if (content.Length == 0) return;

            MemoryItem duplicate = await _db.FindRecentDuplicateAsync(profile.NpcId, content, DateTime.UtcNow.AddMinutes(-10)).ConfigureAwait(false);
            if (duplicate != null) return;

            await _db.AddMemoryAsync(new MemoryItem
            {
                NpcId = profile.NpcId,
                Kind = string.IsNullOrWhiteSpace(kind) ? "event" : kind,
                Content = content,
                Importance = Math.Clamp(importance, 0.05f, 1f),
                Sentiment = Math.Clamp(sentiment, -1f, 1f),
                Keywords = ExtractKeywords(content),
                SubjectNpcId = subjectNpcId,
                ConversationId = conversationId,
                CreatedUtc = DateTime.UtcNow,
                LastAccessedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        public async Task<List<MemoryItem>> RetrieveAsync(NpcProfile profile, string query, int topK, string subjectNpcId = null)
        {
            List<MemoryItem> all = await _db.GetMemoriesAsync(profile.NpcId, 250).ConfigureAwait(false);
            if (all.Count == 0) return all;

            HashSet<string> queryTokens = Tokenize(query);
            DateTime now = DateTime.UtcNow;
            var scored = new List<(MemoryItem Item, double Score)>();

            foreach (MemoryItem m in all)
            {
                double ageDays = Math.Max(0, (now - m.CreatedUtc).TotalDays);
                double recency = Math.Exp(-ageDays / 30.0);
                double relevance = 0;
                if (queryTokens.Count > 0)
                {
                    HashSet<string> tokens = Tokenize(m.Content + " " + m.Keywords);
                    int overlap = queryTokens.Count(t => tokens.Contains(t));
                    relevance = Math.Min(1.0, overlap / (double)Math.Max(2, queryTokens.Count));
                }
                double subjectBoost = !string.IsNullOrEmpty(subjectNpcId) && string.Equals(m.SubjectNpcId, subjectNpcId, StringComparison.Ordinal) ? 0.25 : 0;
                double score = m.Importance * 0.55 + recency * 0.25 + relevance * 0.25 + subjectBoost;
                if (m.Kind == "promise" || m.Kind == "agreement") score += 0.1;
                scored.Add((m, score));
            }

            List<MemoryItem> result = scored
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(1, topK))
                .Select(x => x.Item)
                .ToList();

            await _db.TouchMemoriesAsync(result.Select(r => r.Id)).ConfigureAwait(false);
            return result;
        }

        public async Task SummarizeIfNeededAsync(NpcProfile profile, int maxItems)
        {
            int count = await _db.CountMemoriesAsync(profile.NpcId).ConfigureAwait(false);
            if (count <= maxItems) return;

            List<MemoryItem> memories = await _db.GetMemoriesAsync(profile.NpcId, 400).ConfigureAwait(false);
            var low = memories.Where(m => m.Importance < 0.35f).OrderBy(m => m.CreatedUtc).Take(12).ToList();
            if (low.Count >= 6)
            {
                var text = new StringBuilder("Earlier, I remember: ");
                foreach (MemoryItem m in low.Take(6))
                    text.Append(Trim(m.Content, 90)).Append("; ");
                await AddMemoryAsync(profile, "summary", text.ToString(), 0.45f, 0f, null, null).ConfigureAwait(false);
            }

            await _db.PruneMemoriesAsync(profile.NpcId, maxItems).ConfigureAwait(false);
        }

        public static string FormatForPrompt(List<MemoryItem> memories)
        {
            if (memories == null || memories.Count == 0) return "(no relevant memories)";
            var sb = new StringBuilder();
            foreach (MemoryItem m in memories)
            {
                sb.Append("- ");
                if (!string.IsNullOrEmpty(m.SubjectNpcId)) sb.Append("[about ").Append(m.SubjectNpcId).Append("] ");
                sb.Append(m.Content);
                sb.Append(" (importance ").Append(m.Importance.ToString("0.0")).Append(")");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public static string ExtractKeywords(string text)
        {
            HashSet<string> tokens = Tokenize(text);
            return string.Join(" ", tokens.Take(16));
        }

        public static HashSet<string> Tokenize(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return set;
            foreach (string raw in text.ToLowerInvariant().Split(new[] { ' ', '.', ',', '!', '?', ';', ':', '\'', '"', '\n', '\r', '\t', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw.Length < 3 || StopWords.Contains(raw)) continue;
                set.Add(raw);
            }
            return set;
        }

        private static string BuildInteractionSummary(string playerText, string npcText)
        {
            string p = Trim(playerText, 140);
            string n = Trim(npcText, 140);
            if (string.IsNullOrEmpty(n)) return "The player said to me: \"" + p + "\"";
            return "The player said \"" + p + "\" and I replied \"" + n + "\".";
        }

        private static string Sanitize(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (!char.IsControl(c) || c == '\n' || c == '\t') sb.Append(c);
            return Trim(sb.ToString().Trim(), 500);
        }

        private static string Trim(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "...";
    }
}
