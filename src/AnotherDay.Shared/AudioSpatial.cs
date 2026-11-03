using System;

namespace AnotherDay.Shared
{
    /// <summary>
    /// Distance and direction turned into playback gain and stereo pan.
    ///
    /// The rule is a physical inverse-distance law referenced to a near radius,
    /// faded into a floor at the far radius, so a voice is at full level where
    /// the conversation happens, falls off smoothly as the speaker walks away,
    /// and lands exactly on the floor at the point where it stops being audible
    /// instead of stepping. Nothing here touches audio hardware, so the curve is
    /// unit-tested.
    /// </summary>
    public static class AudioSpatial
    {
        /// <summary>Inside this radius the speaker is at full level.</summary>
        public const float NearDistanceMeters = 3f;

        /// <summary>Beyond this radius the speaker is only just audible.</summary>
        public const float FarDistanceMeters = 45f;

        /// <summary>Gain at the far radius: audible as distance, not as shouting.</summary>
        public const float MinimumGain = 0.06f;

        /// <summary>Never hard-pan; a voice at your shoulder should still feel centred.</summary>
        public const float MaximumPan = 0.8f;

        /// <summary>Seconds for the gain to travel ~63% of the way to a new target.</summary>
        public const double SmoothingSeconds = 0.22;

        public static float Attenuation(float distanceMeters,
            float nearMeters = NearDistanceMeters, float farMeters = FarDistanceMeters)
        {
            if (float.IsNaN(distanceMeters) || distanceMeters < 0f) distanceMeters = 0f;
            if (nearMeters < 0.5f) nearMeters = 0.5f;
            if (farMeters <= nearMeters) farMeters = nearMeters + 1f;

            if (distanceMeters <= nearMeters) return 1f;
            if (distanceMeters >= farMeters) return MinimumGain;

            double t = (distanceMeters - nearMeters) / (farMeters - nearMeters);
            // Gentle inverse-distance curve: 1 at the near radius, ~0.13 at the
            // far radius. A plain near/distance falls too fast right beside the
            // speaker, which is heard as a click when either of you moves.
            double inverse = nearMeters / (nearMeters + (0.5 * (distanceMeters - nearMeters)));
            double fade = 1.0 - (t * t * (3.0 - 2.0 * t));     // smoothstep, 1 -> 0
            double gain = (inverse * fade) + (MinimumGain * (1.0 - fade));
            return (float)gain;
        }

        /// <summary>
        /// Stereo placement from the speaker's direction relative to the
        /// listener's facing: -1 fully left, 0 in front, +1 fully right.
        /// A voice at arm's length is not panned, because at that distance the
        /// direction the head is turned stops matching where the sound is.
        /// </summary>
        public static float Pan(float lateralDot, float distanceMeters,
            float nearMeters = NearDistanceMeters, float farMeters = FarDistanceMeters)
        {
            if (float.IsNaN(lateralDot)) return 0f;
            if (lateralDot > 1f) lateralDot = 1f;
            if (lateralDot < -1f) lateralDot = -1f;

            float falloffStart = nearMeters * 1.15f;
            float falloffEnd = falloffStart + 3f;
            float closeness = distanceMeters <= falloffStart
                ? 0f
                : Math.Min(1f, (distanceMeters - falloffStart) / (falloffEnd - falloffStart));

            // Panning is capped so a distant voice does not jump between ears as
            // the player turns their head.
            float scale = MaximumPan * closeness;
            return lateralDot * scale;
        }

        /// <summary>
        /// Pan turned into per-channel gains, constant power so a voice moving
        /// across the stereo field does not dip in loudness as it passes the
        /// centre. This NAudio build exposes LeftVolume/RightVolume rather than
        /// a Pan property, so the conversion lives here and is tested.
        /// </summary>
        public static void StereoGains(float pan, out float left, out float right)
        {
            if (float.IsNaN(pan)) pan = 0f;
            if (pan < -1f) pan = -1f;
            if (pan > 1f) pan = 1f;
            double angle = (pan + 1.0) * (Math.PI / 4.0);
            left = (float)Math.Cos(angle);
            right = (float)Math.Sin(angle);
        }

        /// <summary>
        /// One smoothing step. Exponential approach in time, so the change is
        /// frame-rate independent and a player sprinting past a speaker hears a
        /// slide rather than a step.
        /// </summary>
        public static float SmoothTowards(float current, float target, double deltaSeconds,
            double timeConstantSeconds = SmoothingSeconds)
        {
            if (timeConstantSeconds <= 0.0001 || deltaSeconds <= 0.0) return target;
            double alpha = 1.0 - Math.Exp(-deltaSeconds / timeConstantSeconds);
            if (alpha < 0.0) alpha = 0.0;
            if (alpha > 1.0) alpha = 1.0;
            return (float)(current + ((target - current) * alpha));
        }
    }

    /// <summary>
    /// The gain and pan that are actually being applied right now, including the
    /// smoothing towards the next target. Lives on the game thread; the audio
    /// thread only ever reads the values it produces.
    /// </summary>
    public sealed class SpatialSmoothing
    {
        private DateTime _lastUtc = DateTime.MinValue;

        public float Gain { get; private set; } = 1f;
        public float Pan { get; private set; }

        public void Reset(float gain, float pan)
        {
            Gain = gain;
            Pan = pan;
            _lastUtc = DateTime.MinValue;
        }

        public void Update(float targetGain, float targetPan, DateTime nowUtc)
        {
            double delta = _lastUtc == DateTime.MinValue
                ? 0.0
                : (nowUtc - _lastUtc).TotalSeconds;
            _lastUtc = nowUtc;

            // A hitch or a paused game must not turn into one huge step.
            if (delta > 0.5) delta = 0.5;
            if (delta < 0.0) delta = 0.0;

            Gain = AudioSpatial.SmoothTowards(Gain, targetGain, delta);
            Pan = AudioSpatial.SmoothTowards(Pan, targetPan, delta);
        }
    }
}
