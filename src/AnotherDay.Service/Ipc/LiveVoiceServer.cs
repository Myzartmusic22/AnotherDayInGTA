using System;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Domain;
using AnotherDay.Service.Live;
using AnotherDay.Shared;

namespace AnotherDay.Service.Ipc
{
    /// <summary>
    /// Duplex pipe that carries a live spoken conversation: microphone PCM up,
    /// Gemini native audio down, plus interruption and transcript events. It is a
    /// separate pipe from the request/response API so 10 audio frames per second
    /// never queue behind a snapshot or a text request.
    /// </summary>
    public sealed class LiveVoiceServer
    {
        private readonly string _pipeName;
        private readonly ServiceState _state;
        private readonly CancellationToken _shutdown;

        public LiveVoiceServer(string pipeName, ServiceState state, CancellationToken shutdown)
        {
            _pipeName = pipeName;
            _state = state;
            _shutdown = shutdown;
        }

        public async Task RunAsync()
        {
            Log.Info("Live voice pipe listening on \\\\.\\pipe\\" + _pipeName);
            while (!_shutdown.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await pipe.WaitForConnectionAsync(_shutdown).ConfigureAwait(false);
                    _ = HandleConnectionAsync(pipe);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (pipe != null) { try { pipe.Dispose(); } catch { } }
                    if (_shutdown.IsCancellationRequested) break;
                    Log.Warn("Live voice accept failed: " + ex.Message);
                    try { await Task.Delay(1000, _shutdown).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            Log.Info("Live voice pipe stopped.");
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
        {
            var writeGate = new SemaphoreSlim(1, 1);
            LiveSession session = null;
            using (pipe)
            using (var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown))
            {
                try
                {
                    MessageEnvelope hello = await ReadAsync(pipe, connectionCts.Token).ConfigureAwait(false);
                    if (hello == null || hello.Type != VoiceMessageTypes.Hello)
                        return;

                    VoiceHelloRequest request = Deserialize<VoiceHelloRequest>(hello);
                    if (request == null || !ConstantTimeEquals(request.Token, _state.Token))
                    {
                        await SendAsync(pipe, writeGate, VoiceMessageTypes.Error,
                            new VoiceHelloResponse { Ok = false, Error = "Invalid voice pipe token." }, connectionCts.Token).ConfigureAwait(false);
                        return;
                    }

                    LiveSessionManager manager = _state.Live;
                    await SendAsync(pipe, writeGate, VoiceMessageTypes.HelloResult, new VoiceHelloResponse
                    {
                        Ok = true,
                        Model = _state.Config.Live.Model,
                        LiveAvailable = manager != null && manager.Available,
                        Reason = manager == null ? "live voice is not initialised" : manager.UnavailableReason
                    }, connectionCts.Token).ConfigureAwait(false);

                    while (!connectionCts.IsCancellationRequested)
                    {
                        MessageEnvelope frame = await ReadAsync(pipe, connectionCts.Token).ConfigureAwait(false);
                        if (frame == null) break;

                        switch (frame.Type)
                        {
                            case VoiceMessageTypes.Start:
                            {
                                if (session != null)
                                    await manager.ReleaseAsync(session.NpcId, "restarted").ConfigureAwait(false);

                                VoiceStartRequest start = Deserialize<VoiceStartRequest>(frame);
                                if (start == null)
                                {
                                    await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, "Malformed start request.", connectionCts.Token)
                                        .ConfigureAwait(false);
                                    continue;
                                }

                                session = await TryStartAsync(pipe, writeGate, start, manager, connectionCts.Token).ConfigureAwait(false);
                                break;
                            }

                            case VoiceMessageTypes.AudioIn:
                            {
                                if (session == null) continue;
                                VoiceAudioChunk chunk = Deserialize<VoiceAudioChunk>(frame);
                                byte[] pcm = chunk?.ToBytes();
                                if (pcm == null || pcm.Length == 0) continue;
                                try
                                {
                                    await session.FeedAsync(pcm, connectionCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    Log.Debug("Live audio send failed: " + ex.Message);
                                }
                                break;
                            }

                            case VoiceMessageTypes.Stop:
                            {
                                if (session != null)
                                {
                                    await manager.ReleaseAsync(session.NpcId, "player stopped").ConfigureAwait(false);
                                    session = null;
                                }
                                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Closed, "stopped", connectionCts.Token).ConfigureAwait(false);
                                break;
                            }

                            case VoiceMessageTypes.TurnEnd:
                            {
                                // Push-to-talk released: close the turn, keep
                                // the session and let the NPC answer fully.
                                if (session == null) continue;
                                try
                                {
                                    await session.EndTurnAsync(connectionCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    Log.Debug("Live turn end failed: " + ex.Message);
                                }
                                break;
                            }

                            default:
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Warn("Live voice connection failed: " + ex.Message);
                }
                finally
                {
                    if (session != null)
                    {
                        try { await _state.Live.ReleaseAsync(session.NpcId, "game disconnected").ConfigureAwait(false); }
                        catch { }
                    }
                }
            }
        }

        /// <summary>Current mood as the animator understands it, or null when neutral.</summary>
        private static string ExpressionFor(ActiveNpc npc)
        {
            EmotionState emotion = npc == null ? null : npc.Emotion;
            if (emotion == null) return null;
            if (emotion.Anger > 0.6f) return "angry";
            if (emotion.Fear > 0.55f) return "nervous";
            if (emotion.Happiness > 0.7f) return "warm";
            if (emotion.Confidence > 0.72f) return "confident";
            return null;
        }

        private async Task<LiveSession> TryStartAsync(NamedPipeServerStream pipe, SemaphoreSlim writeGate,
            VoiceStartRequest start, LiveSessionManager manager, CancellationToken ct)
        {
            if (manager == null)
            {
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, "Live voice is not initialised.", ct).ConfigureAwait(false);
                return null;
            }
            if (!manager.Available)
            {
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, manager.UnavailableReason ?? "Live voice unavailable.", ct).ConfigureAwait(false);
                return null;
            }
            if (!_state.Registry.TryGet(start.NpcHandle, out ActiveNpc npc) || npc == null)
            {
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, "That person is no longer nearby.", ct).ConfigureAwait(false);
                return null;
            }
            if (!string.IsNullOrEmpty(start.NpcId) && !string.Equals(start.NpcId, npc.Profile.NpcId, StringComparison.OrdinalIgnoreCase))
            {
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, "Identity mismatch for that pedestrian.", ct).ConfigureAwait(false);
                return null;
            }

            // The sex comes from the model the player is actually looking at,
            // not from a stored profile that may predate the model check.
            if (start.NpcIsMale.HasValue && npc.Profile.IsMale != start.NpcIsMale.Value)
            {
                Log.Info("Correcting " + npc.Profile.DisplayName + " to " +
                         (start.NpcIsMale.Value ? "male" : "female") + " from the live model.");
                npc.Profile.IsMale = start.NpcIsMale.Value;
                npc.Profile.Voice = await _state.Voices.GetOrAssignAsync(npc.Profile, npc.Emotion).ConfigureAwait(false);
            }

            try
            {
                LiveSession session = await manager.AcquireAsync(npc, start.PlayerArmed, start.PlayerAiming,
                    start.NpcInVehicle, start.NpcIsDriver, start.NpcVehicleModel, start.NpcOnRoad,
                    start.NpcVehiclesNearby, ct).ConfigureAwait(false);

                // Stream model audio and control events to the game as they arrive.
                session.AudioOut += chunk => _ = SendAudioAsync(pipe, writeGate, chunk, ct);
                session.Interrupted += () => _ = SendEventAsync(pipe, writeGate, VoiceEventKinds.Interrupted, null, ct);
                session.Transcript += (role, text) => _ = SendEventAsync(pipe, writeGate,
                    role == "player" ? VoiceEventKinds.InputTranscript : VoiceEventKinds.OutputTranscript, text, ct);

                // A finished turn is liveness, not an ending. The plugin uses it
                // to keep the NPC engaged through the next pause.
                session.TurnCommitted += (playerText, npcText) => _ = SendEventAsync(pipe, writeGate,
                    VoiceEventKinds.TurnComplete, null, ct);
                session.Farewell += speaker => _ = SendEventAsync(pipe, writeGate, VoiceEventKinds.Farewell, speaker, ct);
                session.Closed += reason => _ = SendEventAsync(pipe, writeGate, VoiceEventKinds.SessionEnded, reason, ct);

                await SendAsync(pipe, writeGate, VoiceMessageTypes.Ready, new VoiceReadyResponse
                {
                    ConversationId = start.ConversationId,
                    NpcId = session.NpcId,
                    DisplayName = session.DisplayName,
                    VoiceName = session.VoiceName,
                    Model = session.Model,
                    Resumed = session.Reconnects > 0,
                    Animation = new AnimationStyleDto
                    {
                        IsMale = npc.Profile.IsMale,
                        Friendliness = npc.Profile.Personality.Friendliness,
                        Aggression = npc.Profile.Personality.Aggression,
                        Confidence = npc.Emotion != null ? npc.Emotion.Confidence : npc.Profile.Personality.Confidence,
                        Nervousness = npc.Emotion != null
                            ? Math.Max(npc.Emotion.Fear, npc.Emotion.Stress * 0.8f)
                            : 0.3f,
                        Humor = npc.Profile.Personality.Humor,
                        Expression = ExpressionFor(npc)
                    }
                }, ct).ConfigureAwait(false);

                return session;
            }
            catch (LiveApiException ex)
            {
                Log.Warn("Could not open a live session: " + ex.Message);
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, ex.Message, ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open a live session: " + ex.Message);
                await SendEventAsync(pipe, writeGate, VoiceEventKinds.Error, "Live session failed: " + ex.Message, ct).ConfigureAwait(false);
                return null;
            }
        }

        private static async Task SendEventAsync(NamedPipeServerStream pipe, SemaphoreSlim gate, string kind, string text, CancellationToken ct)
        {
            await SendAsync(pipe, gate, VoiceMessageTypes.Event, new VoiceEventMessage { Kind = kind, Text = text }, ct).ConfigureAwait(false);
        }

        private static async Task SendAudioAsync(NamedPipeServerStream pipe, SemaphoreSlim gate, byte[] pcm, CancellationToken ct)
        {
            await SendAsync(pipe, gate, VoiceMessageTypes.AudioOut, VoiceAudioChunk.From(pcm, 0), ct).ConfigureAwait(false);
        }

        private static async Task SendAsync(NamedPipeServerStream pipe, SemaphoreSlim gate, string type, object payload, CancellationToken ct)
        {
            if (pipe == null || !pipe.IsConnected) return;
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var envelope = new MessageEnvelope
                {
                    Type = type,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    PayloadJson = ServiceState.Serialize(payload)
                };
                await WriteAsync(pipe, envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Live voice write failed: " + ex.Message);
            }
            finally { gate.Release(); }
        }

        private static async Task<MessageEnvelope> ReadAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromMinutes(30));
            return await FrameCodec.ReadFrameAsync(pipe, idle.Token).ConfigureAwait(false);
        }

        private static Task WriteAsync(NamedPipeServerStream pipe, MessageEnvelope envelope, CancellationToken ct)
            => FrameCodec.WriteFrameAsync(pipe, envelope, ct);

        private static T Deserialize<T>(MessageEnvelope envelope) where T : class
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.PayloadJson)) return null;
            try { return ServiceState.Parse<T>(envelope.PayloadJson); }
            catch (Exception ex)
            {
                Log.Debug("Voice frame parse failed: " + ex.Message);
                return null;
            }
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] left = Encoding.UTF8.GetBytes(a);
            byte[] right = Encoding.UTF8.GetBytes(b);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
    }
}
