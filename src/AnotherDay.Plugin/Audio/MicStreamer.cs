using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace AnotherDay.Plugin.Audio
{
    /// <summary>
    /// Continuous 16 kHz mono microphone capture for live conversations. Unlike
    /// the old push-to-talk recorder nothing is written to disk: buffers are
    /// handed straight to the voice link, which streams them to Gemini.
    /// </summary>
    internal sealed class MicStreamer : IDisposable
    {
        public const int SampleRate = 16000;

        private readonly object _sync = new object();
        private readonly string _deviceName;
        private WaveInEvent _waveIn;
        private bool _disposed;

        public MicStreamer(string deviceName)
        {
            _deviceName = deviceName;
        }

        public bool IsRunning { get { lock (_sync) return _waveIn != null; } }
        public string LastError { get; private set; }
        public string DeviceName { get; private set; } = "default input";

        /// <summary>Raised on the capture thread with raw PCM and its RMS (0..1).</summary>
        public event Action<byte[], float> BufferAvailable;

        public static List<string> DeviceNames()
        {
            var names = new List<string>();
            try
            {
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    names.Add(WaveInEvent.GetCapabilities(i).ProductName);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Microphone enumeration failed: " + ex.Message);
            }
            return names;
        }

        public bool Start()
        {
            lock (_sync)
            {
                if (_disposed || _waveIn != null) return true;
                try
                {
                    int deviceIndex = -1;
                    for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    {
                        string name = WaveInEvent.GetCapabilities(i).ProductName ?? "";
                        if (!string.IsNullOrWhiteSpace(_deviceName) &&
                            name.IndexOf(_deviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            deviceIndex = i;
                            DeviceName = name;
                            break;
                        }
                    }

                    _waveIn = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(SampleRate, 16, 1),
                        BufferMilliseconds = 100,
                        NumberOfBuffers = 3
                    };
                    if (deviceIndex >= 0) _waveIn.DeviceNumber = deviceIndex;
                    if (deviceIndex < 0) DeviceName = "default input";

                    _waveIn.DataAvailable += OnDataAvailable;
                    _waveIn.RecordingStopped += OnRecordingStopped;
                    _waveIn.StartRecording();
                    LastError = null;
                    PluginLog.Info("Microphone streaming from " + DeviceName);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "Microphone unavailable: " + ex.Message;
                    PluginLog.Warn(LastError);
                    Cleanup();
                    return false;
                }
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e == null || e.BytesRecorded <= 0) return;
            byte[] copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            float rms = Rms(copy);
            Action<byte[], float> handler = BufferAvailable;
            if (handler == null) return;
            try { handler(copy, rms); }
            catch (Exception ex) { PluginLog.Warn("Microphone buffer handler failed: " + ex.Message); }
        }

        private static float Rms(byte[] pcm16)
        {
            int samples = pcm16.Length / 2;
            if (samples == 0) return 0f;
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                double value = sample / 32768.0;
                sum += value * value;
            }
            return (float)Math.Sqrt(sum / samples);
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e?.Exception != null)
            {
                LastError = "Microphone stopped: " + e.Exception.Message;
                PluginLog.Warn(LastError);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_waveIn == null) return;
                try { _waveIn.StopRecording(); } catch { }
                Cleanup();
            }
        }

        private void Cleanup()
        {
            if (_waveIn != null)
            {
                try { _waveIn.DataAvailable -= OnDataAvailable; } catch { }
                try { _waveIn.RecordingStopped -= OnRecordingStopped; } catch { }
                try { _waveIn.Dispose(); } catch { }
                _waveIn = null;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                Cleanup();
            }
        }
    }
}
