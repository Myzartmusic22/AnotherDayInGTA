using System;
using System.IO;
using NAudio.Wave;

namespace AnotherDay.Plugin.Audio
{
    internal sealed class MicrophoneRecorder : IDisposable
    {
        private readonly string _directory;
        private readonly object _sync = new object();
        private WaveInEvent _waveIn;
        private WaveFileWriter _writer;
        private string _path;
        private DateTime _startedUtc;
        private bool _disposed;

        public MicrophoneRecorder(string directory)
        {
            _directory = directory;
        }

        public bool IsRecording { get { lock (_sync) return _waveIn != null; } }
        public TimeSpan Elapsed { get { lock (_sync) return _waveIn == null ? TimeSpan.Zero : DateTime.UtcNow - _startedUtc; } }
        public string LastError { get; private set; }

        public bool Start()
        {
            lock (_sync)
            {
                if (_disposed || _waveIn != null) return false;
                try
                {
                    Directory.CreateDirectory(_directory);
                    _path = Path.Combine(_directory, "ptt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".wav");
                    _writer = new WaveFileWriter(_path, new WaveFormat(16000, 16, 1));
                    _waveIn = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(16000, 16, 1),
                        BufferMilliseconds = 100,
                        NumberOfBuffers = 3
                    };
                    _waveIn.DataAvailable += OnDataAvailable;
                    _waveIn.RecordingStopped += OnRecordingStopped;
                    _waveIn.StartRecording();
                    _startedUtc = DateTime.UtcNow;
                    LastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "Microphone unavailable: " + ex.Message;
                    PluginLog.Warn(LastError);
                    CleanupLocked();
                    return false;
                }
            }
        }

        public string Stop()
        {
            lock (_sync)
            {
                if (_waveIn == null) return null;
                string path = _path;
                try { _waveIn.StopRecording(); } catch { }
                CleanupLocked();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length < 2048)
                {
                    try { File.Delete(path); } catch { }
                    return null;
                }
                return path;
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            lock (_sync)
            {
                if (_writer == null) return;
                try { _writer.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null) PluginLog.Warn("Microphone stopped with an error: " + e.Exception.Message);
        }

        private void CleanupLocked()
        {
            if (_waveIn != null)
            {
                try { _waveIn.DataAvailable -= OnDataAvailable; } catch { }
                try { _waveIn.RecordingStopped -= OnRecordingStopped; } catch { }
                try { _waveIn.Dispose(); } catch { }
                _waveIn = null;
            }
            if (_writer != null)
            {
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                CleanupLocked();
            }
        }
    }
}
