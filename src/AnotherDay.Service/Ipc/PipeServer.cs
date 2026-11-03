using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Shared;

namespace AnotherDay.Service.Ipc
{
    /// <summary>
    /// Local, current-user-only named pipe. A shared random token is required in
    /// the first hello frame, so another local process cannot drive the service
    /// just by guessing the pipe name.
    /// </summary>
    public sealed class PipeServer
    {
        private readonly string _pipeName;
        private readonly ServiceState _state;
        private readonly CancellationToken _shutdown;
        private readonly HashSet<Task> _connections = new HashSet<Task>();
        private readonly object _connectionLock = new object();

        public PipeServer(string pipeName, ServiceState state, CancellationToken shutdown)
        {
            _pipeName = pipeName;
            _state = state;
            _shutdown = shutdown;
        }

        public int ConnectedClients
        {
            get { lock (_connectionLock) return _connections.Count; }
        }

        public async Task RunAsync()
        {
            Log.Info("IPC listening on pipe \\\\.\\pipe\\" + _pipeName);
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
                    Task task = HandleConnectionAsync(pipe);
                    lock (_connectionLock)
                    {
                        _connections.RemoveWhere(t => t.IsCompleted);
                        _connections.Add(task);
                    }
                    _ = task.ContinueWith(t =>
                    {
                        lock (_connectionLock) _connections.Remove(t);
                    }, TaskScheduler.Default);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (pipe != null) { try { pipe.Dispose(); } catch { } }
                    if (_shutdown.IsCancellationRequested) break;
                    Log.Warn("IPC accept failed: " + ex.Message);
                    try { await Task.Delay(1000, _shutdown).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            Log.Info("IPC server stopped.");
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown))
            {
                bool authenticated = false;
                try
                {
                    while (!connectionCts.IsCancellationRequested)
                    {
                        MessageEnvelope request;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token))
                        {
                            idle.CancelAfter(TimeSpan.FromMinutes(15));
                            request = await FrameCodec.ReadFrameAsync(pipe, idle.Token).ConfigureAwait(false);
                        }
                        if (request == null) break;

                        if (request.Type == MessageTypes.Hello)
                        {
                            MessageEnvelope hello = await _state.HandleAsync(request, connectionCts.Token).ConfigureAwait(false);
                            authenticated = hello.Type == MessageTypes.HelloResult;
                            await FrameCodec.WriteFrameAsync(pipe, hello, connectionCts.Token).ConfigureAwait(false);
                            if (!authenticated) break;
                            continue;
                        }

                        if (!authenticated)
                        {
                            var denied = new MessageEnvelope
                            {
                                Type = MessageTypes.Error,
                                CorrelationId = request.MessageId,
                                PayloadJson = ServiceState.Serialize(new ErrorDto
                                {
                                    Code = "unauthorized",
                                    Message = "Send a valid hello handshake before other requests."
                                })
                            };
                            await FrameCodec.WriteFrameAsync(pipe, denied, connectionCts.Token).ConfigureAwait(false);
                            break;
                        }

                        if (!IsAllowed(request.Type))
                        {
                            var unknown = new MessageEnvelope
                            {
                                Type = MessageTypes.Error,
                                CorrelationId = request.CorrelationId ?? request.MessageId,
                                PayloadJson = ServiceState.Serialize(new ErrorDto
                                {
                                    Code = "unknown_type",
                                    Message = "Unsupported message type: " + request.Type
                                })
                            };
                            await FrameCodec.WriteFrameAsync(pipe, unknown, connectionCts.Token).ConfigureAwait(false);
                            continue;
                        }

                        MessageEnvelope response = await _state.HandleAsync(request, connectionCts.Token).ConfigureAwait(false);
                        await FrameCodec.WriteFrameAsync(pipe, response, connectionCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (EndOfStreamException) { }
                catch (IOException ex)
                {
                    Log.Debug("IPC connection closed: " + ex.Message);
                }
                catch (Exception ex)
                {
                    Log.Warn("IPC connection failed: " + ex.Message);
                }
                finally
                {
                    if (authenticated) Log.Info("Game disconnected from the AI service.");
                }
            }
        }

        private static bool IsAllowed(string type)
        {
            switch (type)
            {
                case MessageTypes.Ping:
                case MessageTypes.PedSnapshot:
                case MessageTypes.Converse:
                case MessageTypes.VoiceConverse:
                case MessageTypes.WorldEvents:
                case MessageTypes.GetSettings:
                case MessageTypes.UpdateSettings:
                case MessageTypes.TriggerSocial:
                case MessageTypes.GetStatus:
                    return true;
                default:
                    return false;
            }
        }
    }
}
