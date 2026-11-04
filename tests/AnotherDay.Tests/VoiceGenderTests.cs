using System.Threading.Tasks;
using AnotherDay.Service.Data;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>
    /// A female pedestrian answered in a masculine voice. These tests pin the
    /// rules that make that impossible again: every Gemini voice has a verified
    /// sex, a character only ever gets a voice of their own sex, and a stored
    /// assignment that breaks the rule is corrected without losing anything else.
    /// </summary>
    public sealed class VoiceGenderTests
    {
        [Fact]
        public void TheVerifiedTableCoversAllThirtyGeminiVoices()
        {
            Assert.Equal(30, GeminiVoices.All.Count);
            Assert.Equal(30, VoiceCatalog.All.Count);
        }

        [Fact]
        public void SadachbiaIsMale()
        {
            // The old catalog called this voice female, and that single wrong
            // label is what put a man's voice on a woman's body.
            Assert.True(GeminiVoices.IsCompatible("Sadachbia", true));
            Assert.False(GeminiVoices.IsCompatible("Sadachbia", false));
            Assert.Equal("male", Find("Sadachbia").Gender);
        }

        [Fact]
        public void TheServiceCatalogAgreesWithTheVerifiedTable()
        {
            foreach (VoiceOption voice in VoiceCatalog.All)
            {
                bool? sex = GeminiVoices.GenderOf(voice.VoiceId);
                Assert.True(sex.HasValue, voice.VoiceId + " is missing from the verified table");
                Assert.Equal(sex.Value, voice.Gender == "male");
            }
        }

        [Fact]
        public void FemaleCharactersAreNeverOfferedAMaleVoice()
        {
            foreach (VoiceOption voice in VoiceCatalog.All)
            {
                bool compatible = GeminiVoices.IsCompatible(voice.VoiceId, false);
                Assert.Equal(voice.Gender == "female", compatible);
            }
        }

        [Fact]
        public void MaleCharactersAreNeverOfferedAFemaleVoice()
        {
            foreach (VoiceOption voice in VoiceCatalog.All)
            {
                bool compatible = GeminiVoices.IsCompatible(voice.VoiceId, true);
                Assert.Equal(voice.Gender == "male", compatible);
            }
        }

        [Fact]
        public void UnknownVoicesAreRejectedInsteadOfGuessed()
        {
            Assert.Null(GeminiVoices.GenderOf("NotAVoice"));
            Assert.False(GeminiVoices.IsCompatible("NotAVoice", true));
            Assert.False(GeminiVoices.IsCompatible(null, false));
            Assert.False(GeminiVoices.IsCompatible("", true));
        }

        [Fact]
        public void ResolutionFallsBackToAVerifiedVoiceOfTheRightSex()
        {
            Assert.Equal("Kore", GeminiVoices.Resolve("Sadachbia", false));
            Assert.Equal("Kore", GeminiVoices.Resolve("Alnilam", false));
            Assert.Equal("Kore", GeminiVoices.Resolve(null, false));
            Assert.Equal("Alnilam", GeminiVoices.Resolve("Zephyr", true));
            Assert.True(GeminiVoices.IsCompatible(GeminiVoices.Resolve("Zephyr", true), true));
            Assert.True(GeminiVoices.IsCompatible(GeminiVoices.Resolve("Sadachbia", false), false));
        }

        [Fact]
        public void CatalogChangesHaveAVersion()
        {
            Assert.True(VoiceCatalog.Version >= 2);
        }

        [Fact]
        public async Task AStoredAssignmentWithTheWrongSexIsCorrected()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();

            NpcProfile profile = new IdentityGenerator().Generate(Female("npc_f_1"), "Strawberry", "1.0", "npc_f_1");
            await db.SaveProfileAsync(profile);

            // Exactly what an older build could have written: a male voice on a
            // female character, stamped with the old catalog version.
            await db.SaveVoiceAssignmentAsync(profile.NpcId, new VoiceAssignment
            {
                VoiceId = "Alnilam",
                VoiceName = "Alnilam",
                Style = "old",
                IsMale = true,
                CatalogVersion = 1,
                AssignedUtc = System.DateTime.UtcNow.ToString("o")
            });

            var voices = new VoiceEngine(db);
            VoiceAssignment corrected = await voices.GetOrAssignAsync(profile, new EmotionState());

            Assert.NotNull(corrected);
            Assert.True(GeminiVoices.IsCompatible(corrected.VoiceId, false));
            Assert.NotEqual("Alnilam", corrected.VoiceId);
            Assert.False(corrected.IsMale);
            Assert.Equal(VoiceCatalog.Version, corrected.CatalogVersion);

            // Persisted, so the next session keeps the corrected voice.
            VoiceAssignment stored = await db.GetVoiceAssignmentAsync(profile.NpcId);
            Assert.Equal(corrected.VoiceId, stored.VoiceId);
        }

        [Fact]
        public async Task AFemaleCharacterKeepsOneFemaleVoiceAcrossSessions()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();

            NpcProfile profile = new IdentityGenerator().Generate(Female("npc_f_2"), "Vinewood", "1.0", "npc_f_2");
            var voices = new VoiceEngine(db);
            VoiceAssignment first = await voices.GetOrAssignAsync(profile, new EmotionState());

            Assert.True(GeminiVoices.IsCompatible(first.VoiceId, false));

            var reopened = new VoiceEngine(new Database(workspace.Paths));
            VoiceAssignment again = await reopened.GetOrAssignAsync(profile, new EmotionState());

            Assert.Equal(first.VoiceId, again.VoiceId);
        }

        [Fact]
        public void ConfidentFemaleIdentityDoesNotGenerateLowPitchDirections()
        {
            NpcProfile profile = new IdentityGenerator().Generate(Female("female-confident"), "Vinewood", "1.0", "npc_f_confident");
            profile.Personality.Confidence = 0.95f;
            profile.Speech.Pitch = "low"; // Also covers profiles created by older builds.

            string style = VoiceEngine.BuildStyle(profile, new EmotionState());

            Assert.DoesNotContain("low pitch", style);
            Assert.DoesNotContain("low and steady", style);
            Assert.Contains("steady and assured", style);
        }

        [Fact]
        public async Task StoredLowRegisterFemaleVoiceIsReassigned()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();
            NpcProfile profile = new IdentityGenerator().Generate(Female("female-low-register"), "Vinewood", "1.0", "npc_f_low");
            profile.Speech.Pitch = "low";
            await db.SaveVoiceAssignmentAsync(profile.NpcId, new VoiceAssignment
            {
                VoiceId = "Gacrux",
                VoiceName = "Gacrux",
                IsMale = false,
                CatalogVersion = VoiceCatalog.Version
            });

            VoiceAssignment corrected = await new VoiceEngine(db).GetOrAssignAsync(profile, new EmotionState());
            VoiceOption selected = Find(corrected.VoiceId);

            Assert.NotNull(selected);
            Assert.False(GeminiVoices.IsCompatible(corrected.VoiceId, true));
            Assert.True(GeminiVoices.IsCompatible(corrected.VoiceId, false));
            Assert.NotEqual("low", selected.Pitch);
            Assert.DoesNotContain("low pitch", corrected.Style);
        }

        private static VoiceOption Find(string voiceId)
        {
            foreach (VoiceOption voice in VoiceCatalog.All)
                if (voice.VoiceId == voiceId) return voice;
            return null;
        }

        private static PedObservation Female(string fingerprint)
        {
            return new PedObservation
            {
                Handle = 501,
                Fingerprint = fingerprint,
                ModelHash = unchecked((int)0x9C9EFFD8),
                ModelName = "a_f_y_business_01",
                IsMale = false,
                CategoryHint = NpcCategories.Civilian,
                Position = new Vec3 { X = 5f, Y = 6f, Z = 7f },
                Health = 200,
                MaxHealth = 200,
                DistanceToPlayer = 2f
            };
        }
    }
}
