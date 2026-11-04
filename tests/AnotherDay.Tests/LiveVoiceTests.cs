using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service;
using AnotherDay.Service.Data;
using AnotherDay.Service.Domain;
using AnotherDay.Service.Live;
using AnotherDay.Service.Security;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>Scripted stand-in for the Gemini WebSocket, so the protocol layer is testable without a key.</summary>
    internal sealed class FakeLiveTransport : ILiveTransport
    {
        private readonly Queue<string> _incoming = new Queue<string>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        public readonly List<string> Sent = new List<string>();
        private bool _closed;

        public bool IsOpen => !_closed;
        public string CloseReason { get; private set; }
        public bool ConnectCalled { get; private set; }
        public Uri ConnectedUri { get; private set; }

        public void ServerSays(string json)
        {
            lock (_incoming) _incoming.Enqueue(json);
            _signal.Release();
        }

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectCalled = true;
            ConnectedUri = uri;
            return Task.CompletedTask;
        }

        public Task SendAsync(string json, CancellationToken ct)
        {
            lock (Sent) Sent.Add(json);
            // Answer the setup immediately with setupComplete.
            if (json.Contains("\"setup\"")) ServerSays("{\"setupComplete\":{}}");
            return Task.CompletedTask;
        }

        public async Task<string> ReceiveAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            lock (_incoming)
            {
                if (_incoming.Count == 0) return null;
                return _incoming.Dequeue();
            }
        }

        public Task CloseAsync(string reason)
        {
            _closed = true;
            CloseReason = reason;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    public class LiveProtocolTests
    {
        private static AppConfig Config()
        {
            var config = new AppConfig { SourcePath = Path.Combine(Path.GetTempPath(), "anotherday-live.json") };
            config.Normalize();
            return config;
        }

        [Fact]
        public void SetupNestsGenerationConfigAndAsksForNativeAudio()
        {
            string json = GeminiLiveClient.BuildSetupJson("gemini-3.8-live", "Kore", "You are Maria.",
                Config().Live, null, flatModalities: false);

            Assert.Contains("\"model\":\"models/gemini-3.8-live\"", json);
            Assert.Contains("\"responseModalities\":[\"AUDIO\"]", json);
            Assert.Contains("\"voiceName\":\"Kore\"", json);
            Assert.Contains("You are Maria.", json);
            Assert.Contains("automaticActivityDetection", json);
            Assert.Contains("generationConfig", json);
            Assert.DoesNotContain("responseModalities\":[\"AUDIO\"],\"systemInstruction\"", json);
        }

        [Fact]
        public void FlatSetupFormKeepsModalitiesAtTheRoot()
        {
            string json = GeminiLiveClient.BuildSetupJson("gemini-3.8-live", "Puck", "Persona",
                Config().Live, "handle-123", flatModalities: true);

            Assert.Contains("\"responseModalities\":[\"AUDIO\"]", json);
            Assert.DoesNotContain("generationConfig", json);
            Assert.Contains("\"handle\":\"handle-123\"", json);
        }

        [Fact]
        public void AudioFramesUseSixteenKhzPcm()
        {
            byte[] pcm = new byte[640];
            string json = GeminiLiveClient.BuildAudioMessage(pcm);
            Assert.Contains("\"mimeType\":\"audio/pcm;rate=16000\"", json);
            Assert.Contains(Convert.ToBase64String(pcm), json);
        }

        [Fact]
        public void ServerAudioIsDecodedAtTwentyFourKhz()
        {
            byte[] pcm = { 1, 2, 3, 4, 5, 6 };
            string json = "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\"" +
                          Convert.ToBase64String(pcm) + "\",\"mimeType\":\"audio/pcm;rate=24000\"}}]},\"turnComplete\":true}}";

            LiveServerEvent evt = GeminiLiveClient.ParseServerMessage(json);
            Assert.True(evt.HasAudio);
            Assert.Equal(pcm, evt.Audio[0]);
            Assert.True(evt.TurnComplete);
        }

        [Fact]
        public void InterruptionTranscriptsAndResumptionAreParsed()
        {
            LiveServerEvent interrupt = GeminiLiveClient.ParseServerMessage("{\"serverContent\":{\"interrupted\":true}}");
            Assert.True(interrupt.Interrupted);

            LiveServerEvent told = GeminiLiveClient.ParseServerMessage(
                "{\"serverContent\":{\"inputTranscription\":{\"text\":\"where is the station\"},\"outputTranscription\":{\"text\":\"two blocks east\"}}}");
            Assert.Equal("where is the station", told.InputTranscript);
            Assert.Equal("two blocks east", told.OutputTranscript);

            LiveServerEvent resume = GeminiLiveClient.ParseServerMessage(
                "{\"sessionResumptionUpdate\":{\"newHandle\":\"abc\",\"resumable\":true}}");
            Assert.Equal("abc", resume.ResumptionHandle);
            Assert.True(resume.Resumable);

            LiveServerEvent away = GeminiLiveClient.ParseServerMessage("{\"goAway\":{\"timeLeft\":\"10s\"}}");
            Assert.True(away.GoAway);
            Assert.Equal("10s", away.GoAwayTime);
        }

        [Fact]
        public void ErrorMessagesAreSurfacedNotSwallowed()
        {
            LiveServerEvent evt = GeminiLiveClient.ParseServerMessage(
                "{\"error\":{\"code\":401,\"message\":\"API key not valid\",\"status\":\"UNAUTHENTICATED\"}}");
            Assert.Contains("API key not valid", evt.ErrorMessage);
            Assert.Contains("UNAUTHENTICATED", evt.ErrorMessage);
        }

        [Fact]
        public async Task ConnectCompletesSetupAndStreamsBothDirections()
        {
            var transport = new FakeLiveTransport();
            using var client = new GeminiLiveClient(Config(), () => transport, "test-key",
                "gemini-3.8-live", "Aoede", "You are a mechanic.");

            var audio = new List<byte[]>();
            var heard = new List<string>();
            bool interrupted = false;
            client.AudioReceived += chunk => { lock (audio) audio.Add(chunk); };
            client.InputTranscript += text => { lock (heard) heard.Add(text); };
            client.Interrupted += () => interrupted = true;

            await client.ConnectAsync(CancellationToken.None);
            Assert.True(client.IsReady);
            Assert.True(transport.ConnectCalled);
            Assert.Contains("key=", transport.ConnectedUri.ToString());
            Assert.Contains("BidiGenerateContent", transport.ConnectedUri.ToString());
            Assert.Contains("models/gemini-3.8-live", transport.Sent[0]);

            await client.SendAudioAsync(new byte[320], CancellationToken.None);
            Assert.Contains(transport.Sent, s => s.Contains("realtimeInput"));

            transport.ServerSays("{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\"" +
                Convert.ToBase64String(new byte[] { 9, 8, 7 }) + "\",\"mimeType\":\"audio/pcm;rate=24000\"}}]}}}");
            transport.ServerSays("{\"serverContent\":{\"interrupted\":true}}");
            transport.ServerSays("{\"serverContent\":{\"inputTranscription\":{\"text\":\"hey\"}}}");

            for (int i = 0; i < 60 && (!interrupted || heard.Count == 0 || audio.Count == 0); i++)
                await Task.Delay(20);

            lock (audio) Assert.Single(audio);
            lock (heard) Assert.Equal("hey", heard[0]);
            Assert.True(interrupted);
        }

        [Fact]
        public async Task PushToTalkReleaseEndsTheTurnWithoutClosingTheSession()
        {
            var transport = new FakeLiveTransport();
            using var client = new GeminiLiveClient(Config(), () => transport, "test-key",
                "gemini-3.8-live", "Aoede", "You are a mechanic.");

            await client.ConnectAsync(CancellationToken.None);
            await client.SendAudioAsync(new byte[320], CancellationToken.None);
            await client.SendAudioStreamEndAsync(CancellationToken.None);

            // Releasing push-to-talk finishes the player's turn; it never tears
            // the session down, so the NPC can still answer.
            Assert.Contains(transport.Sent, s => s.Contains("\"audioStreamEnd\":true"));
            Assert.True(client.IsReady);
        }

        [Fact]
        public async Task FarewellIsRecognizedWhenTranscriptArrivesInWordFragments()
        {
            var transport = new FakeLiveTransport();
            using var session = new LiveSession(Config(),
                async (handle, ct) =>
                {
                    var client = new GeminiLiveClient(Config(), () => transport, "test-key",
                        "gemini-3.8-live", "Aoede", "You are a mechanic.", handle);
                    await client.ConnectAsync(ct);
                    return client;
                },
                "npc_test", "Test NPC", "Aoede", "gemini-3.8-live");
            var farewell = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Farewell += _ => farewell.TrySetResult(true);

            await session.StartAsync(CancellationToken.None);
            transport.ServerSays("{\"serverContent\":{\"inputTranscription\":{\"text\":\"You be\"}}}");
            transport.ServerSays("{\"serverContent\":{\"inputTranscription\":{\"text\":\"safe\"}}}");
            transport.ServerSays("{\"serverContent\":{\"turnComplete\":true}}");

            Task completed = await Task.WhenAny(farewell.Task, Task.Delay(2000));
            Assert.Same(farewell.Task, completed);
            Assert.True(await farewell.Task);
            await session.StopAsync("test complete");
        }

        [Fact]
        public async Task FarewellIsRecognizedWhenTranscriptUpdatesRepeatTheirPrefix()
        {
            var transport = new FakeLiveTransport();
            using var session = new LiveSession(Config(),
                async (handle, ct) =>
                {
                    var client = new GeminiLiveClient(Config(), () => transport, "test-key",
                        "gemini-3.8-live", "Aoede", "You are a mechanic.", handle);
                    await client.ConnectAsync(ct);
                    return client;
                },
                "npc_test", "Test NPC", "Aoede", "gemini-3.8-live");
            var farewell = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Farewell += _ => farewell.TrySetResult(true);

            await session.StartAsync(CancellationToken.None);
            transport.ServerSays("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Take care\"}}}");
            transport.ServerSays("{\"serverContent\":{\"inputTranscription\":{\"text\":\"Take care of yourself.\"}}}");
            transport.ServerSays("{\"serverContent\":{\"turnComplete\":true}}");

            Task completed = await Task.WhenAny(farewell.Task, Task.Delay(2000));
            Assert.Same(farewell.Task, completed);
            Assert.True(await farewell.Task);
            await session.StopAsync("test complete");
        }

        [Fact]
        public async Task NpcFarewellEndsAfterItsCompletedTranscriptAndOrdinaryRepliesDoNot()
        {
            var transport = new FakeLiveTransport();
            using var session = new LiveSession(Config(),
                async (handle, ct) =>
                {
                    var client = new GeminiLiveClient(Config(), () => transport, "test-key",
                        "gemini-3.8-live", "Aoede", "You are a mechanic.", handle);
                    await client.ConnectAsync(ct);
                    return client;
                },
                "npc_test", "Test NPC", "Aoede", "gemini-3.8-live");
            var farewell = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Farewell += speaker => farewell.TrySetResult(speaker);

            await session.StartAsync(CancellationToken.None);
            transport.ServerSays("{\"serverContent\":{\"outputTranscription\":{\"text\":\"I was just getting started.\"}}}");
            transport.ServerSays("{\"serverContent\":{\"turnComplete\":true}}");
            await Task.Delay(100);
            Assert.False(farewell.Task.IsCompleted);

            transport.ServerSays("{\"serverContent\":{\"outputTranscription\":{\"text\":\"All right, take care.\"}}}");
            transport.ServerSays("{\"serverContent\":{\"turnComplete\":true}}");

            Task completed = await Task.WhenAny(farewell.Task, Task.Delay(2000));
            Assert.Same(farewell.Task, completed);
            Assert.Equal("npc", await farewell.Task);
            await session.StopAsync("test complete");
        }

        [Fact]
        public void PersonaCarriesIdentityMoodAndMemory()
        {
            var profile = new NpcProfile
            {
                NpcId = "npc_1",
                DisplayName = "Marcus Webb",
                Age = 41,
                Occupation = "tow truck driver",
                Category = NpcCategories.Civilian,
                PersonalityDescription = "Blunt, tired, quietly funny.",
                Background = "Grew up in Strawberry, still owes money to the wrong people.",
                IsMale = true,
                Personality = new PersonalityTraits { Aggression = 0.7f, Confidence = 0.6f, Humor = 0.4f },
                Voice = new VoiceAssignment { VoiceId = "Alnilam", VoiceName = "Alnilam", Style = "steady and assured" }
            };
            var emotion = new EmotionState { Anger = 0.7f, Trust = 0.2f };
            var relationship = new Relationship { NpcId = "npc_1", TargetId = "player", Familiarity = 0.5f, Affinity = 0.2f };
            var memories = new List<MemoryItem>
            {
                new MemoryItem { Content = "The player helped him push a car off the road." }
            };
            var turns = new List<ConversationTurn>
            {
                new ConversationTurn { NpcId = "npc_1", Speaker = "player", Text = "You alright?" }
            };

            string persona = LivePersona.Build(profile, emotion, relationship, memories, turns,
                "Strawberry", "rain", 22, playerArmed: true, playerAiming: false);

            Assert.Contains("Marcus Webb", persona);
            Assert.Contains("tow truck driver", persona);
            Assert.Contains("angry", persona);
            Assert.Contains("weapon", persona);
            Assert.Contains("helped him push a car", persona);
            Assert.Contains("You alright?", persona);
            Assert.Contains("Strawberry", persona);
            Assert.Contains("acquaintance", persona);
            Assert.Contains("Never invent a vest, uniform, hard hat, tools", persona);
            Assert.Contains("never say you are an AI", persona.Replace("Never", "never"));
        }

        [Fact]
        public async Task VoiceAssignmentIsStableAndNeverAFallbackVoice()
        {
            await using var workspace = new TempWorkspace();
            await using var db = new Database(workspace.Paths);
            await db.InitializeAsync();
            var voices = new VoiceEngine(db);

            var observation = new PedObservation
            {
                Handle = 71,
                Fingerprint = "voice-fingerprint",
                ModelHash = 4242,
                ModelName = "a_f_y_business_01",
                IsMale = false,
                CategoryHint = NpcCategories.Civilian,
                Position = new Vec3 { X = 10f, Y = 10f, Z = 10f },
                Health = 200,
                MaxHealth = 200,
                DistanceToPlayer = 3f
            };
            string npcId = IdentityGenerator.MakeNpcId("a_f_y_business_01|f|4242|1");
            NpcProfile profile = new IdentityGenerator().Generate(observation, "Strawberry", "1.0", npcId);

            VoiceAssignment first = await voices.GetOrAssignAsync(profile, new EmotionState());
            Assert.NotNull(first);
            Assert.False(first.IsFallback);
            Assert.False(first.VoiceId.StartsWith("local-", StringComparison.OrdinalIgnoreCase));

            // A second engine instance (fresh game session) must give the same voice.
            var reloaded = new VoiceEngine(new Database(workspace.Paths));
            VoiceAssignment again = await reloaded.GetOrAssignAsync(profile, new EmotionState());
            Assert.Equal(first.VoiceId, again.VoiceId);
        }

        [Fact]
        public void VoiceChunksRoundTripThroughThePipePayload()
        {
            byte[] pcm = Enumerable.Range(0, 3200).Select(i => (byte)(i % 251)).ToArray();
            VoiceAudioChunk chunk = VoiceAudioChunk.From(pcm, 7);
            Assert.Equal(pcm, chunk.ToBytes());
            Assert.Equal(7, chunk.Sequence);
        }
    }

    /// <summary>
    /// The key is a live credential, so these tests pin its shape rather than
    /// its value: clipboard noise is stripped, and a doubled paste is refused
    /// instead of being stored and sent to Google as a puzzling 401.
    /// </summary>
    public sealed class SecretStoreTests
    {
        [Fact]
        public void ClipboardNoiseIsStrippedFromAKey()
        {
            Assert.Equal("AQ.Ab8RN6Jx_y-123", SecretStore.Sanitize("\uFEFF  \"AQ.Ab8RN6Jx_y-123\"\r\n"));
            Assert.Equal("AIzaSyD-1234", SecretStore.Sanitize("'AIzaSyD-1234'"));
        }

        [Fact]
        public void BothKeyFamiliesAreAccepted()
        {
            Assert.False(SecretStore.LooksMalformed("AQ.Ab8RN6Jx_y-12345678901234567890123456789012", out _));
            Assert.False(SecretStore.LooksMalformed("AIza-not-a-real-test-key", out _));
        }

        [Fact]
        public void ADoubledPasteIsRejectedBeforeItIsStored()
        {
            string doubled = "AQ." + new string('a', 52) + "." + new string('a', 50);
            Assert.True(SecretStore.LooksMalformed(doubled, out string reason));
            Assert.Contains("dots", reason);
            Assert.True(SecretStore.LooksMalformed("AQ.abc def", out _));
            Assert.True(SecretStore.LooksMalformed("AQ." + new string('a', 130), out _));
        }

        [Fact]
        public void FingerprintIdentifiesAKeyWithoutRevealingIt()
        {
            string fingerprint = SecretStore.Fingerprint("AQ.abcdefghij");
            Assert.EndsWith("/13", fingerprint);
            Assert.DoesNotContain("abcdef", fingerprint);
            Assert.Equal("none", SecretStore.Fingerprint(null));
        }
    }
}
