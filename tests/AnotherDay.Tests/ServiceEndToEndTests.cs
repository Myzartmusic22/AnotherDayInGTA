using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service;
using AnotherDay.Service.Ipc;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public class ServiceEndToEndTests
    {
        private const string Token = "test-token-0123456789abcdef";

        static ServiceEndToEndTests()
        {
            // Force the offline path: no provider key is available in tests.
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "");
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "");
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private static PedSnapshotRequest SnapshotRequest()
        {
            var request = new PedSnapshotRequest
            {
                PlayerPosition = new Vec3 { X = 0f, Y = 0f, Z = 0f },
                PlayerHeading = 0f,
                PlayerHandle = 1,
                Zone = "Vinewood",
                Weather = "Clear",
                ClockHour = 14,
                ClockMinute = 30
            };

            string[] models = { "a_m_y_business_01", "a_f_y_hipster_02", "a_m_y_stbla_01" };
            for (int i = 0; i < models.Length; i++)
            {
                request.Peds.Add(new PedObservation
                {
                    Handle = 100 + i,
                    Fingerprint = models[i] + "|" + (i % 2 == 0 ? "m" : "f") + "|4120|" + (i + 1),
                    ModelHash = 4120,
                    ModelName = models[i],
                    IsMale = i % 2 == 0,
                    CategoryHint = NpcCategories.Civilian,
                    Position = new Vec3 { X = 1.5f + i, Y = 0.5f, Z = 0f },
                    Heading = 90f,
                    Health = 200,
                    MaxHealth = 200,
                    ArmorMax = 0,
                    DistanceToPlayer = 1.5f + i,
                    IsOnScreen = true
                });
            }

            return request;
        }

        private static async Task<MessageEnvelope> RoundTripAsync(NamedPipeClientStream pipe, MessageEnvelope request)
        {
            await FrameCodec.WriteFrameAsync(pipe, request, CancellationToken.None);
            return await FrameCodec.ReadFrameAsync(pipe, CancellationToken.None);
        }

        [Fact]
        public async Task IpcPipelineBindsIdentitiesAndAnswersOfflineConversations()
        {
            await using var workspace = new TempWorkspace();
            var state = new ServiceState(workspace.Paths, workspace.Config, Token);
            await state.InitializeAsync();
            Assert.True(state.Database.IsAvailable, state.Database.LastError);

            using var shutdown = new CancellationTokenSource();
            var server = new PipeServer(workspace.Config.Service.PipeName, state, shutdown.Token);
            Task serverTask = server.RunAsync();

            using var pipe = new NamedPipeClientStream(".", workspace.Config.Service.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(8000);

            try
            {
                // --- handshake -------------------------------------------------
                MessageEnvelope hello = await RoundTripAsync(pipe, new MessageEnvelope
                {
                    Type = MessageTypes.Hello,
                    PayloadJson = JsonSerializer.Serialize(new HandshakeRequest
                    {
                        Token = Token,
                        ClientVersion = "test",
                        GameVersion = "1.0.3889.0",
                        ProtocolVersion = Protocol.SchemaVersion
                    }, Json)
                });

                Assert.Equal(MessageTypes.HelloResult, hello.Type);
                HandshakeResponse handshake = JsonSerializer.Deserialize<HandshakeResponse>(hello.PayloadJson, Json);
                Assert.NotNull(handshake);
                Assert.True(handshake.Ok);
                Assert.False(handshake.AiConfigured);

                // --- snapshot / identity ---------------------------------------
                MessageEnvelope snapshotRaw = await RoundTripAsync(pipe, new MessageEnvelope
                {
                    Type = MessageTypes.PedSnapshot,
                    PayloadJson = JsonSerializer.Serialize(SnapshotRequest(), Json)
                });

                Assert.Equal(MessageTypes.PedSnapshotResult, snapshotRaw.Type);
                PedSnapshotResponse snapshot = JsonSerializer.Deserialize<PedSnapshotResponse>(snapshotRaw.PayloadJson, Json);
                Assert.NotNull(snapshot);
                Assert.True(snapshot.Ok, snapshot.Error);
                Assert.Equal(3, snapshot.Bindings.Count);
                Assert.All(snapshot.Bindings, b =>
                {
                    Assert.StartsWith("npc_", b.NpcId);
                    Assert.False(string.IsNullOrWhiteSpace(b.DisplayName));
                    Assert.False(string.IsNullOrWhiteSpace(b.VoiceName));
                });
                Assert.Equal(3, snapshot.KnownNpcCount);

                PedBinding target = snapshot.Bindings[0];
                Assert.True(target.IsNew);

                // --- conversation (offline provider) ---------------------------
                MessageEnvelope converseRaw = await RoundTripAsync(pipe, new MessageEnvelope
                {
                    Type = MessageTypes.Converse,
                    PayloadJson = JsonSerializer.Serialize(new ConversationRequest
                    {
                        NpcHandle = target.Handle,
                        PlayerHandle = 1,
                        PlayerText = "Hey, how long have you lived around here?",
                        ConversationId = "test-conversation",
                        PlayerPosition = new Vec3(),
                        Zone = "Vinewood",
                        Weather = "Clear",
                        ClockHour = 14
                    }, Json)
                });

                Assert.Equal(MessageTypes.ConverseResult, converseRaw.Type);
                ConversationResponse reply = JsonSerializer.Deserialize<ConversationResponse>(converseRaw.PayloadJson, Json);
                Assert.NotNull(reply);
                Assert.True(reply.Ok, reply.Error);
                Assert.Equal(target.NpcId, reply.NpcId);
                Assert.False(string.IsNullOrWhiteSpace(reply.ReplyText));
                Assert.Equal(target.VoiceName, reply.VoiceName);

                // --- memory was actually written -------------------------------
                int memories = await state.Database.CountMemoriesAsync(target.NpcId);
                Assert.True(memories > 0);

                // --- status ----------------------------------------------------
                MessageEnvelope statusRaw = await RoundTripAsync(pipe, new MessageEnvelope
                {
                    Type = MessageTypes.GetStatus,
                    PayloadJson = "{}"
                });
                Assert.Equal(MessageTypes.StatusResult, statusRaw.Type);
                StatusDto status = JsonSerializer.Deserialize<StatusDto>(statusRaw.PayloadJson, Json);
                Assert.NotNull(status);
                Assert.Equal(3, status.KnownNpcs);
            }
            finally
            {
                shutdown.Cancel();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                await state.Database.DisposeAsync();
            }
        }

        [Fact]
        public async Task InvalidTokenIsRejected()
        {
            await using var workspace = new TempWorkspace();
            var state = new ServiceState(workspace.Paths, workspace.Config, Token);
            await state.InitializeAsync();

            using var shutdown = new CancellationTokenSource();
            var server = new PipeServer(workspace.Config.Service.PipeName, state, shutdown.Token);
            Task serverTask = server.RunAsync();

            using var pipe = new NamedPipeClientStream(".", workspace.Config.Service.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(8000);

            try
            {
                MessageEnvelope response = await RoundTripAsync(pipe, new MessageEnvelope
                {
                    Type = MessageTypes.Hello,
                    PayloadJson = JsonSerializer.Serialize(new HandshakeRequest
                    {
                        Token = "wrong-token-0000000000000",
                        ProtocolVersion = Protocol.SchemaVersion
                    }, Json)
                });

                Assert.NotNull(response);
                Assert.NotEqual(MessageTypes.HelloResult, response.Type);
                ErrorDto error = JsonSerializer.Deserialize<ErrorDto>(response.PayloadJson, Json);
                Assert.NotNull(error);
                Assert.Equal("unauthorized", error.Code);
            }
            finally
            {
                shutdown.Cancel();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                await state.Database.DisposeAsync();
            }
        }

        [Fact]
        public async Task IdentitiesAndKnowNpcsPersistAcrossServiceRestart()
        {
            await using var workspace = new TempWorkspace();
            var firstIds = new List<string>();
            var first = new ServiceState(workspace.Paths, workspace.Config, Token);
            await first.InitializeAsync();
            await using (first.Database)
            {
                PedSnapshotResponse before = await first.Registry.ProcessSnapshotAsync(SnapshotRequest(), CancellationToken.None);
                Assert.Equal(3, before.Bindings.Count);
                foreach (PedBinding binding in before.Bindings)
                {
                    Assert.True(binding.IsNew);
                    firstIds.Add(binding.NpcId);
                }
            }

            var second = new ServiceState(workspace.Paths, workspace.Config, Token);
            await second.InitializeAsync();
            await using (second.Database)
            {
                PedSnapshotResponse after = await second.Registry.ProcessSnapshotAsync(SnapshotRequest(), CancellationToken.None);
                Assert.Equal(3, after.Bindings.Count);
                Assert.Equal(
                    firstIds.OrderBy(id => id),
                    after.Bindings.Select(b => b.NpcId).OrderBy(id => id));
                foreach (PedBinding binding in after.Bindings)
                {
                    Assert.False(binding.IsNew);
                    Assert.True(binding.IsKnown);
                    Assert.True(binding.Confidence >= 0.9, "confidence was " + binding.Confidence);
                }

                int profiles = await second.Database.CountProfilesAsync();
                Assert.Equal(3, profiles);
            }
        }
    }
}
