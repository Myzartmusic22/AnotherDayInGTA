using System;
using AnotherDay.Shared;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AnotherDay.Plugin.Audio
{
    /// <summary>
    /// Streams Gemini Live native audio (24 kHz, 16-bit, mono PCM) straight into
    /// the game's sound device as it arrives. No file is written and no speech
    /// synthesiser is involved: these are the model's own audio samples.
    ///
    /// The mono stream is placed in stereo and attenuated by distance to the
    /// speaker, so a pedestrian's voice comes from where they are standing
    /// instead of being squirted at the player at a fixed level. Gain and pan
    /// are smoothed (AudioSpatial.SmoothTowards) so walking towards or past
    /// somebody slides instead of stepping.
    /// </summary>
    internal sealed class PcmPlayer : IDisposable
    {
        public const int SampleRate = 24000;

        private readonly object _sync = new object();
        private readonly BufferedWaveProvider _buffer;
        private readonly MonoToStereoSampleProvider _stereo;
        private readonly VolumeSampleProvider _volume;
        private readonly WaveOutEvent _output;
        private readonly SpatialSmoothing _spatial = new SpatialSmoothing();
        private float _gain = 1f;
        private bool _disposed;

        public PcmPlayer()
        {
            _buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(12),
                DiscardOnBufferOverflow = true,
                ReadFully = true // silence instead of stalls between chunks
            };
            _stereo = new MonoToStereoSampleProvider(_buffer.ToSampleProvider());
            _volume = new VolumeSampleProvider(_stereo) { Volume = 1f };
            _output = new WaveOutEvent { DesiredLatency = 90, NumberOfBuffers = 3 };
            _output.Init(_volume.ToWaveProvider16());
        }

        public bool IsPlaying
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _buffer.BufferedDuration > TimeSpan.FromMilliseconds(60);
                }
            }
        }

        /// <summary>Master level: the config/panel volume, not the distance.</summary>
        public void SetVolume(float volume)
        {
            lock (_sync)
            {
                _gain = Math.Min(1f, Math.Max(0f, volume));
                ApplyLocked();
            }
        }

        /// <summary>
        /// Places the voice at its speaker: <paramref name="gain"/> from the
        /// distance curve, <paramref name="pan"/> from the direction (-1 left,
        /// +1 right). Both are approached smoothly over a fraction of a second.
        /// </summary>
        public void SetSpatial(float gain, float pan, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _spatial.Update(Math.Min(1f, Math.Max(0f, gain)),
                    Math.Min(1f, Math.Max(-1f, pan)), nowUtc);
                ApplyLocked();
            }
        }

        /// <summary>Drops the distance smoothing, e.g. when a speaker changes.</summary>
        public void ResetSpatial()
        {
            lock (_sync) _spatial.Reset(1f, 0f);
        }

        private void ApplyLocked()
        {
            try
            {
                _volume.Volume = _gain * _spatial.Gain;
                float left, right;
                AudioSpatial.StereoGains(_spatial.Pan, out left, out right);
                _stereo.LeftVolume = left;
                _stereo.RightVolume = right;
            }
            catch { }
        }

        public void Play(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length == 0) return;
            lock (_sync)
            {
                if (_disposed) return;
                try
                {
                    _buffer.AddSamples(pcm16, 0, pcm16.Length);
                    if (_output.PlaybackState != PlaybackState.Playing) _output.Play();
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("Live voice playback failed: " + ex.Message);
                }
            }
        }

        /// <summary>Drops everything queued, which is what an interruption sounds like.</summary>
        public void Flush()
        {
            lock (_sync)
            {
                if (_disposed) return;
                try { _buffer.ClearBuffer(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try { _output.Stop(); } catch { }
                try { _output.Dispose(); } catch { }
                try { _buffer.ClearBuffer(); } catch { }
            }
        }
    }
}
