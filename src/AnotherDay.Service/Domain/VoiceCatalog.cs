using System.Collections.Generic;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    /// <summary>
    /// The 30 prebuilt Gemini TTS studio voices. Assignments are persistent;
    /// dozens of NPCs share a voice rather than each requiring a custom model.
    ///
    /// The gender column must match Gemini's published voice table exactly: it
    /// decides which voice a character is allowed to speak with. Sadachbia was
    /// labelled female here and is male in that table, which is how a female
    /// pedestrian answered in a man's voice.
    /// </summary>
    public static class VoiceCatalog
    {
        /// <summary>Version of the gender classifications below. Assignments
        /// stored against an older version are re-validated on use.</summary>
        public static int Version { get { return GeminiVoices.CatalogVersion; } }

        public static readonly IReadOnlyList<VoiceOption> All = new List<VoiceOption>
        {
            V("Zephyr", "female", "Bright", "high", "American", "bright and airy", 0.7f),
            V("Puck", "male", "Upbeat", "medium", "American", "upbeat and energetic", 0.85f),
            V("Charon", "male", "Informative", "low", "American", "steady and informative", 0.45f),
            V("Kore", "female", "Firm", "medium", "American", "firm and grounded", 0.55f),
            V("Fenrir", "male", "Excitable", "low", "American", "excitable and rough", 0.8f),
            V("Leda", "female", "Youthful", "high", "American", "youthful and light", 0.7f),
            V("Orus", "male", "Firm", "low", "American", "firm and deliberate", 0.5f),
            V("Aoede", "female", "Breezy", "medium", "American", "breezy and casual", 0.65f),
            V("Callirrhoe", "female", "Easy-going", "medium", "American", "easy-going", 0.45f),
            V("Autonoe", "female", "Bright", "medium", "American", "bright and curious", 0.7f),
            V("Enceladus", "male", "Breathy", "low", "American", "breathy and quiet", 0.3f),
            V("Iapetus", "male", "Clear", "medium", "American", "clear and plain", 0.5f),
            V("Umbriel", "male", "Easy-going", "low", "American", "relaxed and slow", 0.35f),
            V("Algieba", "male", "Smooth", "low", "American", "smooth and confident", 0.55f),
            V("Despina", "female", "Smooth", "medium", "American", "smooth and measured", 0.5f),
            V("Erinome", "female", "Clear", "high", "American", "clear and precise", 0.55f),
            V("Algenib", "male", "Gravelly", "low", "American", "gravelly and worn", 0.6f),
            V("Rasalgethi", "male", "Informative", "medium", "American", "matter-of-fact", 0.45f),
            V("Laomedeia", "female", "Upbeat", "high", "American", "lively and chatty", 0.75f),
            V("Achernar", "female", "Soft", "medium", "American", "soft and gentle", 0.35f),
            V("Alnilam", "male", "Firm", "low", "American", "firm and blunt", 0.6f),
            V("Schedar", "male", "Even", "medium", "American", "even and neutral", 0.4f),
            V("Gacrux", "female", "Mature", "low", "American", "mature and dry", 0.45f),
            V("Pulcherrima", "female", "Forward", "medium", "American", "forward and direct", 0.7f),
            V("Achird", "male", "Friendly", "medium", "American", "friendly and warm", 0.65f),
            V("Zubenelgenubi", "male", "Casual", "low", "American", "casual and laid-back", 0.45f),
            V("Vindemiatrix", "female", "Gentle", "medium", "American", "gentle and careful", 0.3f),
            V("Sadachbia", "male", "Lively", "high", "American", "lively and sharp", 0.8f),
            V("Sadaltager", "male", "Knowledgeable", "medium", "American", "knowledgeable and precise", 0.45f),
            V("Sulafat", "female", "Warm", "low", "American", "warm and reassuring", 0.4f)
        };

        private static VoiceOption V(string name, string gender, string description, string pitch, string accent, string persona, float energy)
        {
            return new VoiceOption
            {
                VoiceId = name,
                Name = name,
                Gender = gender,
                Description = description,
                Pitch = pitch,
                Accent = accent,
                Persona = persona,
                Energy = energy,
                IsFallback = false
            };
        }
    }
}
