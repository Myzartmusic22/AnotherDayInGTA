using System;
using System.Collections.Generic;
using System.IO;
using AnotherDay.Shared;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AnotherDay.Plugin.Audio
{
    /// <summary>
    /// Sequential playback for pre-rendered NPC lines: lines never overlap.
    ///
    /// The current line is placed in stereo and attenuated by distance, using
    /// the same curves as the live voice player, so a pedestrian talking to
    /// another pedestrian is heard from where they stand. The speaker's handle
    /// travels with the line: the caller resolves the position each tick and
    /// hands back a gain and a pan, which keeps this class free of game access
    /// and safe to drive from the game thread only.
    /// </summary>
    internal sealed class AudioPlayback : IDisposable
    {
        private sealed class Item
        {
            public string Path;
            public float Volume;
            public string Text;
            public int SpeakerHandle;
        }

        private readonly Queue<Item> _queue = new Queue<Item>();
        private readonly object _sync = new object();
        private readonly SpatialSmoothing _spatial = new SpatialSmoothing();
        private WaveOutEvent _output;
        private AudioFileReader _reader;
        private VolumeSampleProvider _volumeProvider;
        private MonoToStereoSampleProvider _stereo;
        private float _lineVolume = 1f;
        private float _masterVolume = 1f;
        private int _speakerHandle;
        private bool _disposed;

        public event Action<string> LineStarted;

        public bool IsPlaying { get { lock (_sync) return _output != null; } }

        /// <summary>Pedestrian whose line is currently playing, 0 when none.</summary>
        public int CurrentSpeakerHandle { get { lock (_sync) return _output != null ? _speakerHandle : 0; } }

        public void SetVolume(float volume)
        {
            _masterVolume = Math.Min(1f, Math.Max(0f, volume));
            lock (_sync) ApplyLocked();
        }

        public void Enqueue(string path, string text, float volume, int speakerHandle = 0)
        {
            if (_disposed || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            lock (_sync)
            {
                _queue.Enqueue(new Item
                {
                    Path = path,
                    Volume = Math.Min(1f, Math.Max(0f, volume)),
                    Text = text,
                    SpeakerHandle = speakerHandle
                });
            }
            StartNext();
        }

        /// <summary>
        /// Called from the game tick for the line that is playing. Gain and pan
        /// are smoothed here, so a player driving past a talking pedestrian hears
        /// the voice slide away instead of clicking.
        /// </summary>
        public void UpdateSpatial(float targetGain, float targetPan, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (_disposed || _output == null) return;
                _spatial.Update(Math.Min(1f, Math.Max(0f, targetGain)),
                    Math.Min(1f, Math.Max(-1f, targetPan)), nowUtc);
                ApplyLocked();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _queue.Clear();
                StopCurrentLocked();
            }
        }

        private void ApplyLocked()
        {
            try
            {
                if (_volumeProvider != null)
                    _volumeProvider.Volume = _masterVolume * _lineVolume * _spatial.Gain;
                if (_stereo != null)
                {
                    float left, right;
                    AudioSpatial.StereoGains(_spatial.Pan, out left, out right);
                    _stereo.LeftVolume = left;
                    _stereo.RightVolume = right;
                }
            }
            catch { }
        }

        private void StartNext()
        {
            Item next = null;
            lock (_sync)
            {
                if (_disposed || _output != null) return;
                while (_queue.Count > 0 && next == null)
                {
                    Item candidate = _queue.Dequeue();
                    if (candidate != null && File.Exists(candidate.Path)) next = candidate;
                }
                if (next == null) return;

                try
                {
                    _reader = new AudioFileReader(next.Path);
                    ISampleProvider samples = _reader;
                    _stereo = null;
                    if (_reader.WaveFormat.Channels == 1)
                    {
                        // Rendered lines are mono; widen them so they can be
                        // panned without collapsing to one ear.
                        _stereo = new MonoToStereoSampleProvider(samples);
                        samples = _stereo;
                    }
                    _lineVolume = next.Volume;
                    _speakerHandle = next.SpeakerHandle;
                    _volumeProvider = new VolumeSampleProvider(samples)
                    {
                        Volume = _masterVolume * _lineVolume * _spatial.Gain
                    };

                    _output = new WaveOutEvent();
                    _output.PlaybackStopped += OnPlaybackStopped;
                    _output.Init(_volumeProvider.ToWaveProvider16());
                    _output.Play();
                    ApplyLocked();
                    LineStarted?.Invoke(next.Text);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("Voice playback failed: " + ex.Message);
                    StopCurrentLocked();
                }
            }

            if (_queue.Count > 0) StartNext();
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            lock (_sync) StopCurrentLocked();
            StartNext();
        }

        private void StopCurrentLocked()
        {
            if (_output != null)
            {
                try { _output.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { _output.Stop(); } catch { }
                try { _output.Dispose(); } catch { }
                _output = null;
            }
            if (_reader != null)
            {
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }
            _volumeProvider = null;
            _stereo = null;
            _speakerHandle = 0;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _queue.Clear();
                StopCurrentLocked();
            }
        }
    }
}
