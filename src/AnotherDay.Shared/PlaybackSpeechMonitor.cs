using System;
using System.Threading;

namespace AnotherDay.Shared
{
    /// <summary>
    /// Tracks speech in the samples consumed by the output device, rather than
    /// when audio chunks arrive from the network. A generated chunk can arrive
    /// ahead of playback and sit in the queue, so network-time estimates make
    /// lip-sync lag behind what the player actually hears.
    /// </summary>
    public sealed class PlaybackSpeechMonitor
    {
        private readonly float _threshold;
        private int _speaking;

        public PlaybackSpeechMonitor(float threshold = AnimationSettings.DefaultMouthLevel)
        {
            _threshold = Math.Max(0f, threshold);
        }

        public bool IsSpeaking
        {
            get { return Interlocked.CompareExchange(ref _speaking, 0, 0) != 0; }
        }

        /// <summary>
        /// Called on the audio render path. Performs bounded arithmetic over
        /// the supplied block and communicates the result with one atomic write.
        /// It allocates nothing and takes no locks.
        /// </summary>
        public void Record(float[] samples, int offset, int count)
        {
            if (samples == null || offset < 0 || count <= 0 || offset > samples.Length - count)
            {
                Interlocked.Exchange(ref _speaking, 0);
                return;
            }

            double sum = 0;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                double sample = samples[i];
                sum += sample * sample;
            }

            double rms = Math.Sqrt(sum / count);
            Interlocked.Exchange(ref _speaking, rms >= _threshold ? 1 : 0);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _speaking, 0);
        }
    }
}
