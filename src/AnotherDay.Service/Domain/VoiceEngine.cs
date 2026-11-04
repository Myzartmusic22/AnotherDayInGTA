using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnotherDay.Service.Data;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    /// <summary>
    /// Assigns a stable voice identity to each NPC. A voice is chosen once from
    /// the 30 prebuilt Gemini voices (matching the ped's sex and balanced across
    /// the population) and persisted. Voices are never regenerated per
    /// utterance; only the delivery style changes with emotion.
    ///
    /// Compatibility is checked on every use, not just at assignment time. A
    /// stored assignment can be wrong for two reasons: the character's sex was
    /// misclassified when it was created, or the catalog's gender table has
    /// since been corrected. Either way the fix is a voice change only - never a
    /// memory, relationship or personality reset.
    /// </summary>
    public sealed class VoiceEngine
    {
        private readonly Database _db;
        private readonly object _sync = new object();
        private readonly Dictionary<string, VoiceAssignment> _cache = new Dictionary<string, VoiceAssignment>(StringComparer.OrdinalIgnoreCase);

        public VoiceEngine(Database db)
        {
            _db = db;
        }

        public async Task<VoiceAssignment> GetOrAssignAsync(NpcProfile profile, EmotionState emotion)
        {
            if (profile == null) return null;

            lock (_sync)
            {
                VoiceAssignment cached;
                if (_cache.TryGetValue(profile.NpcId, out cached))
                {
                    if (IsSuitable(cached, profile))
                    {
                        cached.Style = BuildStyle(profile, emotion);
                        return cached;
                    }
                    _cache.Remove(profile.NpcId);
                }
            }

            VoiceAssignment existing = await _db.GetVoiceAssignmentAsync(profile.NpcId).ConfigureAwait(false);
            if (existing != null)
            {
                if (IsSuitable(existing, profile))
                {
                    existing.Style = BuildStyle(profile, emotion);
                    existing.IsMale = profile.IsMale;
                    if (existing.CatalogVersion <= 0) existing.CatalogVersion = VoiceCatalog.Version;
                    lock (_sync) _cache[profile.NpcId] = existing;
                    return existing;
                }

                Log.Warn("Voice " + existing.VoiceId + " (" + (existing.IsMale ? "male" : "female") +
                    ") does not fit " + profile.DisplayName + " (" + (profile.IsMale ? "male" : "female") +
                    "). Assigning a new voice; memories and relationships are untouched.");
                return await AssignAsync(profile, emotion,
                    "corrected incompatible voice " + existing.VoiceId).ConfigureAwait(false);
            }

            return await AssignAsync(profile, emotion, "first assignment").ConfigureAwait(false);
        }

        /// <summary>Guarantees a usable, sex-compatible voice for this character.</summary>
        public Task<VoiceAssignment> EnsureCompatibleAsync(NpcProfile profile, EmotionState emotion)
        {
            return GetOrAssignAsync(profile, emotion);
        }

        public async Task<VoiceAssignment> ReassignAsync(NpcProfile profile, EmotionState emotion, string reason)
        {
            if (profile == null) return null;
            lock (_sync) _cache.Remove(profile.NpcId);
            return await AssignAsync(profile, emotion, reason ?? "reassigned").ConfigureAwait(false);
        }

        public static bool IsCompatible(VoiceAssignment assignment, bool isMale)
        {
            return assignment != null && GeminiVoices.IsCompatible(assignment.VoiceId, isMale);
        }

        private static bool IsSuitable(VoiceAssignment assignment, NpcProfile profile)
        {
            if (assignment == null || profile == null || !IsCompatible(assignment, profile.IsMale))
                return false;
            VoiceOption option = VoiceCatalog.All.FirstOrDefault(v =>
                string.Equals(v.VoiceId, assignment.VoiceId, StringComparison.OrdinalIgnoreCase));
            return profile.IsMale || option == null ||
                !string.Equals(option.Pitch, "low", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSuitable(VoiceOption option, NpcProfile profile)
        {
            if (option == null || profile == null ||
                !GeminiVoices.IsCompatible(option.VoiceId, profile.IsMale))
                return false;
            if (profile.IsMale) return true;

            VoiceOption canonical = VoiceCatalog.All.FirstOrDefault(v =>
                string.Equals(v.VoiceId, option.VoiceId, StringComparison.OrdinalIgnoreCase));
            return canonical == null ||
                !string.Equals(canonical.Pitch, "low", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<VoiceAssignment> AssignAsync(NpcProfile profile, EmotionState emotion, string reason)
        {
            List<VoiceOption> catalog = await _db.GetVoiceCatalogAsync().ConfigureAwait(false);
            if (catalog.Count == 0) catalog = VoiceCatalog.All.ToList();
            Dictionary<string, int> usage = await _db.GetVoiceUsageCountsAsync().ConfigureAwait(false);

            // The classification in GeminiVoices is the authority. The database
            // row is only used for description and pitch; a stale gender column
            // must never decide who sounds like a man.
            List<VoiceOption> candidates = catalog
                .Where(v => !v.IsFallback && IsSuitable(v, profile))
                .ToList();
            if (candidates.Count == 0)
                candidates = catalog.Where(v => IsSuitable(v, profile)).ToList();
            if (candidates.Count == 0)
            {
                // Should be impossible with the built-in catalog, but a fallback
                // that is the right sex is always better than a wrong voice.
                VoiceOption safe = new VoiceOption
                {
                    VoiceId = GeminiVoices.DefaultFor(profile.IsMale),
                    Name = GeminiVoices.DefaultFor(profile.IsMale),
                    Gender = profile.IsMale ? "male" : "female",
                    Pitch = "medium",
                    Accent = "American",
                    Description = "safe default",
                    Energy = 0.5f
                };
                candidates.Add(safe);
            }

            var ordered = candidates
                .OrderBy(v => FitPenalty(v, profile))
                .ThenBy(v => usage.TryGetValue(v.VoiceId, out int count) ? count : 0)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int window = Math.Min(5, ordered.Count);
            ulong seed = HashSeed(profile.NpcId + "|voice|" + (profile.IsMale ? "m" : "f") + "|" + VoiceCatalog.Version);
            int pick = (int)(seed % (ulong)Math.Max(1, window));
            VoiceOption chosen = ordered[pick];

            var assignment = new VoiceAssignment
            {
                Provider = "gemini",
                VoiceId = chosen.VoiceId,
                VoiceName = chosen.Name,
                Description = chosen.Description,
                Accent = chosen.Accent,
                Style = BuildStyle(profile, emotion),
                IsFallback = false,
                AssignedUtc = DateTime.UtcNow.ToString("o"),
                IsMale = profile.IsMale,
                CatalogVersion = VoiceCatalog.Version,
                Reason = reason + "; deterministic least-used " + (profile.IsMale ? "male" : "female") +
                         " match for " + profile.Category + " personality profile."
            };

            await _db.SaveVoiceAssignmentAsync(profile.NpcId, assignment).ConfigureAwait(false);
            lock (_sync) _cache[profile.NpcId] = assignment;
            Log.Info("Assigned voice " + chosen.Name + " (" + (profile.IsMale ? "male" : "female") + ") to " +
                     profile.DisplayName + " (" + profile.NpcId + ")");
            return assignment;
        }

        public static string BuildStyle(NpcProfile profile, EmotionState emotion)
        {
            if (profile == null) return "sound natural";

            var traits = profile.Personality ?? new PersonalityTraits();
            var parts = new List<string>();
            parts.Add(emotion != null ? emotion.ToneInstruction() : "sound natural");
            parts.Add(RoleStyle(profile.Category));

            if (traits.Confidence > 0.7f) parts.Add("steady and assured");
            else if (traits.Confidence < 0.3f) parts.Add("quiet and hesitant");

            if (traits.Aggression > 0.65f) parts.Add("with a hard edge");
            else if (traits.Friendliness > 0.7f) parts.Add("warm and open");
            else if (traits.Suspicion > 0.65f) parts.Add("guarded and clipped");

            if (profile.Speech != null)
            {
                if (profile.Speech.Pace == "slow") parts.Add("slow, deliberate pacing");
                else if (profile.Speech.Pace == "quick" || profile.Speech.Pace == "rapid") parts.Add("quick pacing");
                if (profile.Speech.Pitch == "low" && profile.IsMale) parts.Add("low pitch");
                if (profile.Speech.SentenceLength == "short") parts.Add("short, natural sentences");
            }

            string accent = AccentForArea(profile.Neighborhood);
            if (accent != "neutral Los Santos") parts.Add(accent);

            return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
        }

        /// <summary>
        /// Regional speech is grounded in the character's recorded location,
        /// never inferred from race, appearance, or a generated name.
        /// </summary>
        public static string AccentForArea(string area)
        {
            string normalized = (area ?? "").ToLowerInvariant();
            if (normalized.Contains("sandy shores") || normalized.Contains("grapeseed") ||
                normalized.Contains("paleto bay") || normalized.Contains("grand senora") ||
                normalized.Contains("blaine county") || normalized.Contains("harmony"))
                return "subtle rural county drawl";

            return "neutral Los Santos";
        }

        private static string RoleStyle(string category)
        {
            switch (category)
            {
                case NpcCategories.Police:
                    return "professional, clipped, and controlled";
                case NpcCategories.Gang:
                    return "guarded, casual, streetwise vocabulary; use slang sparingly and only when it fits";
                case NpcCategories.Business:
                    return "polished, measured, professional vocabulary";
                case NpcCategories.Driver:
                    return "practical, roadwise, conversational";
                case NpcCategories.Mechanic:
                    return "direct, practical, blue-collar vocabulary";
                case NpcCategories.Paramedic:
                case NpcCategories.Firefighter:
                    return "calm, clear, and practical";
                case NpcCategories.Shopkeeper:
                case NpcCategories.Employee:
                    return "grounded, approachable, conversational";
                case NpcCategories.Security:
                    return "alert, concise, and professional";
                default:
                    return "natural, individual, and conversational";
            }
        }

        public static string BuildFallbackStyle(NpcProfile profile, EmotionState emotion)
        {
            return BuildStyle(profile, emotion);
        }

        private static int FitPenalty(VoiceOption voice, NpcProfile profile)
        {
            int penalty = 0;
            if (profile.Speech == null) return penalty;
            string pitch = profile.Speech.Pitch ?? "medium";
            if (!profile.IsMale && string.Equals(pitch, "low", StringComparison.OrdinalIgnoreCase))
                pitch = "medium";
            if (!string.IsNullOrEmpty(voice.Pitch) && !string.Equals(voice.Pitch, pitch, StringComparison.OrdinalIgnoreCase)) penalty++;
            if (profile.Personality.Confidence > 0.7f && voice.Pitch == "high") penalty++;
            if (profile.Personality.Sociability > 0.7f && voice.Energy < 0.4f) penalty++;
            if (profile.Personality.Aggression > 0.7f && voice.Energy < 0.4f) penalty++;
            if (profile.Category == NpcCategories.Gang && voice.Energy > 0.8f) penalty++;
            return penalty;
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
