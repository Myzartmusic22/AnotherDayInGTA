using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherDay.Service.Live
{
    /// <summary>
    /// The minimum a Live conversation needs from a socket. Abstracted so the
    /// protocol layer can be exercised in tests with scripted server frames
    /// instead of a live Gemini connection.
    /// </summary>
    public interface ILiveTransport : IDisposable
    {
        bool IsOpen { get; }
        string CloseReason { get; }
        Task ConnectAsync(Uri uri, CancellationToken ct);
        Task SendAsync(string json, CancellationToken ct);
        /// <summary>Returns the next complete message, or null when the socket closed.</summary>
        Task<string> ReceiveAsync(CancellationToken ct);
        Task CloseAsync(string reason);
    }

    /// <summary>
    /// ClientWebSocket transport. Gemini sends JSON text frames, but some server
    /// builds send binary frames, so both are decoded as UTF-8 and fragments are
    /// reassembled before parsing.
    /// </summary>
    public sealed class WebSocketLiveTransport : ILiveTransport
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly MemoryStream _buffer = new MemoryStream();
        private readonly byte[] _chunk = new byte[16 * 1024];

        public bool IsOpen => _socket.State == WebSocketState.Open;

        public string CloseReason { get; private set; }

        public WebSocketLiveTransport()
        {
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        }

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

        public async Task SendAsync(string json, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(json)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        public async Task<string> ReceiveAsync(CancellationToken ct)
        {
            _buffer.SetLength(0);
            while (true)
            {
                WebSocketReceiveResult result = await _socket
                    .ReceiveAsync(new ArraySegment<byte>(_chunk), ct)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseReason = "server closed: " + _socket.CloseStatus + " " + _socket.CloseStatusDescription;
                    return null;
                }

                _buffer.Write(_chunk, 0, result.Count);
                if (!result.EndOfMessage) continue;

                return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            }
        }

        public async Task CloseAsync(string reason)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason ?? "done", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { }
        }

        public void Dispose()
        {
            try { _socket.Dispose(); } catch { }
        }
    }
}
