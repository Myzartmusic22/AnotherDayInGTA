using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Shared;
using Newtonsoft.Json;

namespace AnotherDay.Plugin.Ipc
{
    /// <summary>
    /// Duplex pipe to the service's live voice server: microphone PCM goes up,
    /// Gemini's native audio and conversation events come down. Everything is
    /// asynchronous; the game thread only ever enqueues a buffer.
    /// </summary>
    internal sealed class VoiceLink : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly BlockingCollection<MessageEnvelope> _outbox =
            new BlockingCollection<MessageEnvelope>(new ConcurrentQueue<MessageEnvelope>(), 48);
        private CancellationTokenSource _cts;
        private Task _reader;
        private Task _writer;
        private int _sequence;
        private bool _disposed;

        private VoiceLink(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
        }

        public VoiceHelloResponse Hello { get; private set; }
        public bool IsConnected => !_disposed && _pipe != null && _pipe.IsConnected;

        public event Action<VoiceReadyResponse> Ready;
        public event Action<byte[]> AudioReceived;
        public event Action<string, string> EventReceived; // kind, text
        public event Action<string> Closed;

        public static async Task<VoiceLink> ConnectAsync(string pipeName, string tokenPath, CancellationToken ct)
        {
            if (!File.Exists(tokenPath)) throw new IOException("The AI service has not started yet (IPC token missing).");
            string token = File.ReadAllText(tokenPath).Trim();
            if (token.Length < 16) throw new IOException("The IPC token is invalid.");

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);

                var hello = new MessageEnvelope
                {
                    Type = VoiceMessageTypes.Hello,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    PayloadJson = JsonConvert.SerializeObject(new VoiceHelloRequest
                    {
                        Token = token,
                        ClientVersion = "0.1.0",
                        ProtocolVersion = Protocol.SchemaVersion
                    }, PipeClient.JsonSettings)
                };

                await FrameCodec.WriteFrameAsync(pipe, hello, ct).ConfigureAwait(false);
                MessageEnvelope response = await FrameCodec.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                if (response == null) throw new IOException("No answer from the voice server.");

                var link = new VoiceLink(pipe);
                if (response.Type == VoiceMessageTypes.HelloResult)
                {
                    link.Hello = PipeClient.ParsePayload<VoiceHelloResponse>(response);
                }
                else
                {
                    ErrorDto error = null;
                    try { error = PipeClient.ParsePayload<ErrorDto>(response); } catch { }
                    throw new IOException(error?.Message ?? "The voice server rejected this client.");
                }

                link.Start(ct);
                return link;
            }
            catch
            {
                try { pipe.Dispose(); } catch { }
                throw;
            }
        }

        private void Start(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
            _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        }

        public Task SendStartAsync(VoiceStartRequest request, CancellationToken ct)
        {
            Enqueue(VoiceMessageTypes.Start, request, required: true);
            return Task.CompletedTask;
        }

        /// <summary>Queues one microphone buffer. Never blocks the audio callback.</summary>
        public void SendAudio(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length == 0) return;
            int sequence = Interlocked.Increment(ref _sequence);
            Enqueue(VoiceMessageTypes.AudioIn, VoiceAudioChunk.From(pcm16, sequence), required: false);
        }

        public void SendStop()
        {
            Enqueue(VoiceMessageTypes.Stop, new { }, required: false);
        }

        /// <summary>
        /// Push-to-talk released. Queued ahead of any later audio so the model
        /// closes the player's turn and answers the whole sentence.
        /// </summary>
        public void SendTurnEnd()
        {
            Enqueue(VoiceMessageTypes.TurnEnd, new { }, required: false);
        }

        private void Enqueue(string type, object payload, bool required)
        {
            if (_disposed) return;
            var envelope = new MessageEnvelope
            {
                Type = type,
                CorrelationId = Guid.NewGuid().ToString("N"),
                PayloadJson = JsonConvert.SerializeObject(payload, PipeClient.JsonSettings)
            };
            try
            {
                if (_outbox.TryAdd(envelope)) return;
                if (!required)
                {
                    // Audio is time-sensitive: drop the oldest queued buffer rather
                    // than send stale speech late.
                    _outbox.TryTake(out _);
                    _outbox.TryAdd(envelope);
                }
            }
            catch (InvalidOperationException) { /* completed */ }
        }

        private async Task WriteLoopAsync(CancellationToken ct)
        {
            try
            {
                foreach (MessageEnvelope envelope in _outbox.GetConsumingEnumerable(ct))
                {
                    await FrameCodec.WriteFrameAsync(_pipe, envelope, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed) Closed?.Invoke("voice link write failed: " + ex.Message);
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    MessageEnvelope frame = await FrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
                    if (frame == null) break;
                    Dispatch(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_disposed) PluginLog.Debug("Voice link closed: " + ex.Message);
            }
            finally
            {
                if (!_disposed) Closed?.Invoke("voice link closed");
            }
        }

        private void Dispatch(MessageEnvelope frame)
        {
            switch (frame.Type)
            {
                case VoiceMessageTypes.Ready:
                {
                    VoiceReadyResponse ready = PipeClient.ParsePayload<VoiceReadyResponse>(frame);
                    if (ready != null) Ready?.Invoke(ready);
                    break;
                }
                case VoiceMessageTypes.AudioOut:
                {
                    VoiceAudioChunk chunk = PipeClient.ParsePayload<VoiceAudioChunk>(frame);
                    byte[] pcm = chunk?.ToBytes();
                    if (pcm != null && pcm.Length > 0) AudioReceived?.Invoke(pcm);
                    break;
                }
                case VoiceMessageTypes.Event:
                {
                    VoiceEventMessage evt = PipeClient.ParsePayload<VoiceEventMessage>(frame);
                    if (evt != null) EventReceived?.Invoke(evt.Kind, evt.Text);
                    break;
                }
                case VoiceMessageTypes.Error:
                {
                    ErrorDto error = PipeClient.ParsePayload<ErrorDto>(frame);
                    EventReceived?.Invoke(VoiceEventKinds.Error, error?.Message ?? "voice error");
                    break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { _outbox.CompleteAdding(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _outbox.Dispose();
        }
    }
}
