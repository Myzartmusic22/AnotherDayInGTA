using System;
using System.Collections.Generic;

namespace AnotherDay.Shared
{
    /// <summary>
    /// The verified male/female classification of Gemini's 30 prebuilt voices.
    ///
    /// This exists because a female pedestrian answered in a masculine voice:
    /// the old catalog labelled "Sadachbia" female, but Gemini's own voice table
    /// and published usage both list Sadachbia as male. A single wrong label is
    /// enough to break the promise that a character sounds like the body the
    /// player is looking at, so the classification lives in one place, is
    /// versioned, and is checked every time a voice is used.
    ///
    /// Source: Gemini API speech-generation voice table (30 prebuilt studio
    /// voices with a Gender column). Unknown ids are treated as incompatible
    /// rather than guessed.
    /// </summary>
    public static class GeminiVoices
    {
        /// <summary>Bump whenever a classification changes, so stored
        /// assignments written against an older table are re-validated.</summary>
        public const int CatalogVersion = 2;

        public const string DefaultMale = "Alnilam";
        public const string DefaultFemale = "Kore";

        private static readonly Dictionary<string, bool> Sexes =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "Zephyr", false },
                { "Puck", true },
                { "Charon", true },
                { "Kore", false },
                { "Fenrir", true },
                { "Leda", false },
                { "Orus", true },
                { "Aoede", false },
                { "Callirrhoe", false },
                { "Autonoe", false },
                { "Enceladus", true },
                { "Iapetus", true },
                { "Umbriel", true },
                { "Algenib", true },
                { "Despina", false },
                { "Erinome", false },
                { "Laomedeia", false },
                { "Achernar", false },
                { "Algieba", true },
                { "Schedar", true },
                { "Gacrux", false },
                { "Pulcherrima", false },
                { "Achird", true },
                { "Zubenelgenubi", true },
                { "Vindemiatrix", false },
                { "Sadachbia", true },
                { "Sadaltager", true },
                { "Sulafat", false },
                { "Alnilam", true },
                { "Rasalgethi", true }
            };

        public static IReadOnlyDictionary<string, bool> All { get { return Sexes; } }

        /// <summary>True for a recognised male voice, false for a recognised
        /// female voice, null when the id is not one of Gemini's voices.</summary>
        public static bool? GenderOf(string voiceId)
        {
            if (string.IsNullOrWhiteSpace(voiceId)) return null;
            bool male;
            return Sexes.TryGetValue(voiceId.Trim(), out male) ? (bool?)male : null;
        }

        /// <summary>
        /// A voice may be used for a character only when it is a known Gemini
        /// voice of the same sex. Unknown voices are rejected: a voice that does
        /// not exist is silence, and a voice we cannot classify could be the
        /// wrong one.
        /// </summary>
        public static bool IsCompatible(string voiceId, bool isMale)
        {
            bool? gender = GenderOf(voiceId);
            return gender.HasValue && gender.Value == isMale;
        }

        public static string DefaultFor(bool isMale)
        {
            return isMale ? DefaultMale : DefaultFemale;
        }

        /// <summary>Returns the voice to actually use: the assigned one when it
        /// fits, otherwise a verified default for that sex.</summary>
        public static string Resolve(string assignedVoiceId, bool isMale)
        {
            return IsCompatible(assignedVoiceId, isMale) ? assignedVoiceId.Trim() : DefaultFor(isMale);
        }
    }
}
