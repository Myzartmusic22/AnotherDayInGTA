using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    public class AudioSpatialTests
    {
        [Fact]
        public void SpeakerAtYourShoulderIsAtFullLevel()
        {
            Assert.Equal(1f, AudioSpatial.Attenuation(0.4f));
            Assert.Equal(1f, AudioSpatial.Attenuation(AudioSpatial.NearDistanceMeters));
        }

        [Fact]
        public void SpeakerAtTheFarRadiusIsAtTheFloor()
        {
            Assert.Equal(AudioSpatial.MinimumGain, AudioSpatial.Attenuation(AudioSpatial.FarDistanceMeters));
            Assert.Equal(AudioSpatial.MinimumGain, AudioSpatial.Attenuation(120f));
        }

        [Fact]
        public void LevelFallsMonotonicallyWithDistance()
        {
            float previous = 1f;
            for (float distance = 3f; distance <= 45f; distance += 0.5f)
            {
                float gain = AudioSpatial.Attenuation(distance);
                Assert.True(gain <= previous + 0.0001f,
                    "gain rose at " + distance + "m: " + previous + " -> " + gain);
                previous = gain;
            }
        }

        [Fact]
        public void WalkingAwayIsSmoothNotStepped()
        {
            float previous = AudioSpatial.Attenuation(0f);
            for (float distance = 0.25f; distance <= 60f; distance += 0.25f)
            {
                float gain = AudioSpatial.Attenuation(distance);
                Assert.True(Math.Abs(gain - previous) < 0.05f,
                    "jump of " + Math.Abs(gain - previous) + " at " + distance + "m");
                previous = gain;
            }
        }

        [Fact]
        public void NearbySpeakerIsLouderThanADistantFenceSitter()
        {
            float close = AudioSpatial.Attenuation(4f);
            float medium = AudioSpatial.Attenuation(12f);
            float far = AudioSpatial.Attenuation(30f);
            Assert.True(close > medium && medium > far,
                "expected close > medium > far, got " + close + "/" + medium + "/" + far);
            Assert.True(medium < 0.8f, "a speaker 12m away should already be quieter");
        }

        [Fact]
        public void PanFollowsSideAndIsNeverFull()
        {
            Assert.True(AudioSpatial.Pan(1f, 10f) > 0f);
            Assert.True(AudioSpatial.Pan(-1f, 10f) < 0f);
            Assert.Equal(0f, AudioSpatial.Pan(0f, 10f));
            Assert.True(Math.Abs(AudioSpatial.Pan(1f, 10f)) <= AudioSpatial.MaximumPan);
        }

        [Fact]
        public void SomeoneRightNextToYouIsNotPanned()
        {
            Assert.Equal(0f, AudioSpatial.Pan(1f, 1f));
            Assert.Equal(0f, AudioSpatial.Pan(1f, AudioSpatial.NearDistanceMeters));
        }

        [Fact]
        public void SmoothingMovesTowardsTheTargetAndSettles()
        {
            float gain = 1f;
            for (int i = 0; i < 120; i++) gain = AudioSpatial.SmoothTowards(gain, 0.2f, 1.0 / 60.0);
            Assert.InRange(gain, 0.19f, 0.21f);

            float single = AudioSpatial.SmoothTowards(1f, 0f, 1.0 / 60.0);
            Assert.True(single > 0.8f && single < 1f, "one frame must not jump, got " + single);
        }

        [Fact]
        public void SmoothingStateSurvivesALongFrameWithoutJumping()
        {
            var smoothing = new SpatialSmoothing();
            DateTime now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

            smoothing.Update(0.1f, -0.8f, now);
            Assert.Equal(0.1f, smoothing.Gain); // first call has no history: snap

            smoothing.Update(1f, 0f, now.AddSeconds(5));
            Assert.True(smoothing.Gain > 0.8f && smoothing.Gain < 1f,
                "a five second hitch must not count as instant, got " + smoothing.Gain);

            for (int i = 0; i < 240; i++) smoothing.Update(0.3f, 0.5f, now.AddSeconds(5 + (i / 60.0)));
            Assert.InRange(smoothing.Gain, 0.29f, 0.31f);
            Assert.InRange(smoothing.Pan, 0.49f, 0.51f);
        }
    }
}
