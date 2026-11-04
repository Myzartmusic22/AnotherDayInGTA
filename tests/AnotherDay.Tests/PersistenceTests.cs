using System;
using System.IO;
using System.Threading.Tasks;
using AnotherDay.Service;
using AnotherDay.Service.Data;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public sealed class TempWorkspace : IAsyncDisposable
    {
        public AppPaths Paths { get; }
        public AppConfig Config { get; }

        public TempWorkspace()
        {
            Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "anotherday-tests", Guid.NewGuid().ToString("N")));
            Paths.EnsureCreated();
            Config = new AppConfig { SourcePath = Paths.ConfigPath };
            Config.Service.PipeName = "AnotherDayTests-" + Guid.NewGuid().ToString("N");
            Config.Normalize();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
            }
            catch { }
            return ValueTask.CompletedTask;
        }
    }

    public class PersistenceTests
    {
        private static PedObservation Observation(string fingerprint, int modelHash, bool male = true)
        {
            return new PedObservation
            {
                Handle = 1001,
                Fingerprint = fingerprint,
                ModelHash = modelHash,
                ModelName = male ? "a_m_y_business_01" : "a_f_y_business_01",
                IsMale = male,
                CategoryHint = NpcCategories.Civilian,
                Position = new Vec3 { X = 120f, Y = -300f, Z = 30f },
                Health = 200,
                MaxHealth = 200,
                DistanceToPlayer = 4f
            };
        }

        [Fact]
        public void IdentityIsDeterministicForTheSameFingerprint()
        {
            string idA = IdentityGenerator.MakeNpcId("a_m_y_business_01|m|4120|1");
            string idB = IdentityGenerator.MakeNpcId("a_m_y_business_01|m|4120|1");
            string idC = IdentityGenerator.MakeNpcId("a_m_y_business_01|m|4120|2");

            Assert.Equal(idA, idB);
            Assert.NotEqual(idA, idC);
            Assert.StartsWith("npc_", idA);
        }

        [Fact]
        public void GeneratedProfilesAreCoherentAndStable()
        {
            var generator = new IdentityGenerator();
            PedObservation observation = Observation("a_m_y_business_01|m|4120|1", 4120);
            string npcId = IdentityGenerator.MakeNpcId(observation.Fingerprint);

            NpcProfile first = generator.Generate(observation, "Vinewood", "1.0.3889.0", npcId);
            NpcProfile second = generator.Generate(observation, "Vinewood", "1.0.3889.0", npcId);

            Assert.Equal(npcId, first.NpcId);
            Assert.False(string.IsNullOrWhiteSpace(first.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(first.Occupation));
            Assert.False(string.IsNullOrWhiteSpace(first.Background));
            Assert.Equal(first.DisplayName, second.DisplayName);
            Assert.Equal(first.Occupation, second.Occupation);
            Assert.Equal(first.Personality.Friendliness, second.Personality.Friendliness);
            Assert.InRange(first.Personality.Aggression, 0f, 1f);
            Assert.InRange(first.Age, 18, 80);
            Assert.Contains(first.Category, new[]
            {
                NpcCategories.Civilian, NpcCategories.Shopkeeper, NpcCategories.Employee,
                NpcCategories.Business, NpcCategories.Driver, NpcCategories.Passenger,
                NpcCategories.Police, NpcCategories.Gang
            });
        }

        [Fact]
        public async Task ProfileEmotionMemoryAndVoiceSurviveReopen()
        {
            await using var workspace = new TempWorkspace();
            string npcId;
            long memoryId;

            await using (var db = new Database(workspace.Paths))
            {
                await db.InitializeAsync();
                Assert.True(db.IsAvailable, db.LastError);

                var generator = new IdentityGenerator();
                PedObservation observation = Observation("a_f_y_business_01|f|4121|9", 4121, male: false);
                npcId = IdentityGenerator.MakeNpcId(observation.Fingerprint);
                NpcProfile profile = generator.Generate(observation, "Del Perro", "1.0.3889.0", npcId);
                await db.SaveProfileAsync(profile);

                var voiceEngine = new VoiceEngine(db);
                var assignment = await voiceEngine.GetOrAssignAsync(profile, new EmotionState());
                Assert.False(string.IsNullOrWhiteSpace(assignment.VoiceId));

                var emotions = new EmotionEngine();
                EmotionState emotion = emotions.Baseline(profile);
                emotion.Anger = 0.42f;
                await db.SaveEmotionAsync(npcId, emotion);

                memoryId = await db.AddMemoryAsync(new MemoryItem
                {
                    NpcId = npcId,
                    Kind = "conversation",
                    Content = "The player apologised for bumping into them outside the cafe.",
                    Importance = 0.7f,
                    Sentiment = 0.5f,
                    SubjectNpcId = "player"
                });
                Assert.True(memoryId > 0);

                await db.SaveRelationshipAsync(new Relationship
                {
                    NpcId = npcId,
                    TargetId = "player",
                    Trust = 0.6f,
                    Affinity = 0.4f,
                    Status = "acquaintance"
                });
            }

            await using (var reopened = new Database(workspace.Paths))
            {
                await reopened.InitializeAsync();
                Assert.True(reopened.IsAvailable);

                NpcProfile profile = await reopened.GetProfileAsync(npcId);
                Assert.NotNull(profile);
                Assert.Equal(npcId, profile.NpcId);
                Assert.Equal("Del Perro", profile.Neighborhood);

                EmotionState emotion = await reopened.GetEmotionAsync(npcId);
                Assert.NotNull(emotion);
                Assert.InRange(emotion.Anger, 0.3f, 0.55f);

                var memories = await reopened.GetMemoriesAsync(npcId);
                Assert.Contains(memories, m => m.Id == memoryId && m.Content.Contains("apologised"));

                Relationship relationship = await reopened.GetRelationshipAsync(npcId, "player");
                Assert.NotNull(relationship);
                Assert.Equal("acquaintance", relationship.Status);

                VoiceAssignment voice = await reopened.GetVoiceAssignmentAsync(npcId);
                Assert.NotNull(voice);
                Assert.False(string.IsNullOrWhiteSpace(voice.VoiceId));
            }
        }

        [Fact]
        public async Task ReturningPolicePedRepairsPreviouslySavedCivilianIdentity()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();
            Assert.True(db.IsAvailable, db.LastError);

            var observation = Observation("police-model|uniform|1", 5100);
            var generator = new IdentityGenerator();
            string npcId = IdentityGenerator.MakeNpcId(observation.Fingerprint);
            NpcProfile stale = generator.Generate(observation, "Mission Row", "test", npcId);
            await db.SaveProfileAsync(stale);

            var registry = new NpcRegistry(db, generator, new EmotionEngine(), new VoiceEngine(db), workspace.Config);
            observation.CategoryHint = NpcCategories.Police;
            var request = new PedSnapshotRequest { Zone = "Mission Row" };
            request.Peds.Add(observation);

            PedSnapshotResponse response = await registry.ProcessSnapshotAsync(request, default);
            NpcProfile repaired = await db.GetProfileAsync(npcId);

            Assert.Equal(NpcCategories.Police, response.Bindings[0].Category);
            Assert.Equal("police officer", response.Bindings[0].Occupation);
            Assert.Equal(NpcCategories.Police, repaired.Category);
            Assert.Equal("police officer", repaired.Occupation);
            Assert.Contains("professional", repaired.Voice.Style);
        }

        [Fact]
        public async Task ReturningCivilianPedRepairsPreviouslyGeneratedJobGuess()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();
            var observation = Observation("civilian-model|outfit|1", 5101);
            observation.ModelName = "model_5101";
            var generator = new IdentityGenerator();
            string npcId = IdentityGenerator.MakeNpcId(observation.Fingerprint);
            NpcProfile stale = generator.Generate(observation, "Strawberry", "test", npcId);
            stale.Occupation = "barista";
            stale.Background += " Works as a barista.";
            await db.SaveProfileAsync(stale);

            var registry = new NpcRegistry(db, generator, new EmotionEngine(), new VoiceEngine(db), workspace.Config);
            observation.CategoryHint = NpcCategories.Civilian;
            var request = new PedSnapshotRequest { Zone = "Strawberry" };
            request.Peds.Add(observation);

            PedSnapshotResponse response = await registry.ProcessSnapshotAsync(request, default);
            NpcProfile repaired = await db.GetProfileAsync(npcId);

            Assert.Equal("local resident", response.Bindings[0].Occupation);
            Assert.Equal("local resident", repaired.Occupation);
            Assert.DoesNotContain("Works as a barista", repaired.Background);
            Assert.Contains("exact occupation is not established", repaired.Background);
        }

        [Fact]
        public async Task DuplicateMemoriesAreDetectable()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();

            var memory = new MemoryItem
            {
                NpcId = "npc_test",
                Kind = "conversation",
                Content = "The player asked about the shooting near the pier.",
                Importance = 0.5f
            };
            await db.AddMemoryAsync(memory);

            MemoryItem duplicate = await db.FindRecentDuplicateAsync("npc_test", memory.Content, DateTime.UtcNow.AddMinutes(-5));
            Assert.NotNull(duplicate);
            Assert.Equal(memory.Content, duplicate.Content);

            MemoryItem unrelated = await db.FindRecentDuplicateAsync("npc_test", "Completely different subject", DateTime.UtcNow.AddMinutes(-5));
            Assert.Null(unrelated);
        }

        // MarkDormantAsync used to run its updates while its own reader still held
        // the connection gate, which deadlocked the shared connection and froze
        // every later database call in the service.
        [Fact]
        public async Task MarkDormantReleasesTheConnectionForLaterQueries()
        {
            // Runs on a worker so a regression fails the test with a timeout
            // instead of hanging the whole run (the deadlock also blocks dispose).
            Task work = Task.Run(async () =>
            {
                await using var workspace = new TempWorkspace();
                var db = new Database(workspace.Paths);
                await db.InitializeAsync();
                try
                {
                    var generator = new IdentityGenerator();
                    for (int i = 0; i < 3; i++)
                    {
                        PedObservation observation = Observation("a_m_y_business_01|m|4120|" + i, 4120);
                        string npcId = IdentityGenerator.MakeNpcId(observation.Fingerprint);
                        await db.SaveProfileAsync(generator.Generate(observation, "Vinewood", "1.0.3889.0", npcId));
                    }

                    await db.MarkDormantAsync(new[] { "npc_unknown" });
                    int count = await db.CountProfilesAsync();
                    Assert.Equal(3, count);
                }
                finally
                {
                    await db.DisposeAsync();
                }
            });

            await work.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
}
