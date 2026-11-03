using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Shared;
using Newtonsoft.Json;

namespace AnotherDay.Plugin.Ipc
{
    internal sealed class PipeClient : IDisposable
    {
        private readonly string _pipeName;
        private readonly string _tokenPath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private bool _disposed;

        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None
        };

        public PipeClient(string pipeName, string tokenPath)
        {
            _pipeName = pipeName;
            _tokenPath = tokenPath;
        }

        public bool IsConnected => _pipe != null && _pipe.IsConnected;
        public HandshakeResponse Handshake { get; private set; }
        public StatusDto LastStatus { get; set; }
        public string LastError { get; private set; }

        public async Task<MessageEnvelope> SendAsync(MessageEnvelope request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(request.CorrelationId)) request.CorrelationId = request.MessageId;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool retried = false;
                while (true)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PipeClient));
                    await EnsureConnectedAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await FrameCodec.WriteFrameAsync(_pipe, request, ct).ConfigureAwait(false);
                        MessageEnvelope response = await FrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
                        if (response == null) throw new IOException("The AI service closed the connection.");
                        LastError = null;
                        return response;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (!retried && (ex is IOException || ex is ObjectDisposedException))
                    {
                        retried = true;
                        LastError = ex.Message;
                        ClosePipe();
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Connects and performs the hello exchange. Everything here is genuinely
        /// asynchronous: blocking on the same pipe handle with GetAwaiter()
        /// .GetResult() wedges the handle after the first frame, which left the
        /// plugin stuck on "offline" with snapshots that never completed and no
        /// error to show for it.
        /// </summary>
        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_pipe != null && _pipe.IsConnected) return;
            ClosePipe();

            if (!File.Exists(_tokenPath))
                throw new IOException("The AI service has not started yet (IPC token missing).");
            string token = File.ReadAllText(_tokenPath).Trim();
            if (token.Length < 16) throw new IOException("The IPC token is invalid.");

            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2500, ct).ConfigureAwait(false);
                var hello = new MessageEnvelope
                {
                    Type = MessageTypes.Hello,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    PayloadJson = JsonConvert.SerializeObject(new HandshakeRequest
                    {
                        Token = token,
                        ClientVersion = "0.1.0",
                        GameVersion = GameVersion(),
                        ProtocolVersion = Protocol.SchemaVersion
                    }, JsonSettings)
                };

                await FrameCodec.WriteFrameAsync(pipe, hello, ct).ConfigureAwait(false);
                MessageEnvelope response = await FrameCodec.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                if (response == null) throw new IOException("No handshake response.");

                if (response.Type == MessageTypes.HelloResult)
                {
                    Handshake = JsonConvert.DeserializeObject<HandshakeResponse>(response.PayloadJson, JsonSettings);
                    if (Handshake == null || !Handshake.Ok) throw new IOException(Handshake?.Error ?? "Handshake rejected.");
                }
                else
                {
                    ErrorDto error = null;
                    try { error = JsonConvert.DeserializeObject<ErrorDto>(response.PayloadJson, JsonSettings); } catch { }
                    throw new IOException(error?.Message ?? "Handshake rejected.");
                }

                _pipe = pipe;
                LastError = null;
            }
            catch
            {
                try { pipe.Dispose(); } catch { }
                throw;
            }
        }

        private void ClosePipe()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private static string GameVersion()
        {
            try
            {
                return GTA.Game.Version.ToString();
            }
            catch { return "unknown"; }
        }

        public static T ParsePayload<T>(MessageEnvelope envelope) where T : class
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.PayloadJson)) return null;
            return JsonConvert.DeserializeObject<T>(envelope.PayloadJson, JsonSettings);
        }

        public void Dispose()
        {
            _disposed = true;
            try { _gate.Wait(500); } catch { }
            ClosePipe();
            _gate.Dispose();
        }
    }
}
