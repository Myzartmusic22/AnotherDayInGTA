using System;
using AnotherDay.Shared;
using Xunit;

namespace AnotherDay.Tests
{
    /// <summary>
    /// The gesture pass used to name clips that do not exist in GTA V, so every
    /// TASK_PLAY_ANIM was an invisible no-op and the NPC stood still while the
    /// audio played. These tests keep the clip names real and sex-appropriate.
    /// </summary>
    public sealed class ConversationGestureClipTests
    {
        private static readonly ConversationGesture[] EveryGesture =
            (ConversationGesture[])Enum.GetValues(typeof(ConversationGesture));

        private static ConversationStyle Style(bool male)
        {
            return new ConversationStyle { IsMale = male };
        }

        [Fact]
        public void EveryGestureHasAPlayableVanillaClipForBothSexes()
        {
            foreach (bool male in new[] { true, false })
            {
                ConversationStyle style = Style(male);
                foreach (ConversationGesture gesture in EveryGesture)
                {
                    if (gesture == ConversationGesture.None) continue;

                    GestureClip clip = style.Gesture(gesture);

                    Assert.NotNull(clip);
                    Assert.False(string.IsNullOrWhiteSpace(clip.Dictionary));
                    Assert.False(string.IsNullOrWhiteSpace(clip.Clip));
                    Assert.True(clip.Clip.StartsWith("gesture_", StringComparison.Ordinal) ||
                        clip.Clip == "indicate_right", "unexpected clip: " + clip.Clip);
                }
            }
        }

        [Fact]
        public void GestureDictionariesMatchThePedSex()
        {
            Assert.Equal("gestures@m@standing@casual", Style(true).GestureDictionary);
            Assert.Equal("gestures@f@standing@casual", Style(false).GestureDictionary);
        }

        [Fact]
        public void ExplainUsesTheCasualDictionaryForTheRightSex()
        {
            GestureClip male = Style(true).Gesture(ConversationGesture.Explain);
            GestureClip female = Style(false).Gesture(ConversationGesture.Explain);

            Assert.Equal("gestures@m@standing@casual", male.Dictionary);
            Assert.Equal("gestures@f@standing@casual", female.Dictionary);
            Assert.NotEqual(male.Clip, female.Clip);
        }

        [Fact]
        public void PointUsesADictionaryThatExistsForBothSexes()
        {
            // The casual sets differ by sex and the male set has no point clip,
            // so pointing uses the gun-shop tutorial, which both models have.
            GestureClip male = Style(true).Gesture(ConversationGesture.Point);
            GestureClip female = Style(false).Gesture(ConversationGesture.Point);

            Assert.Equal("mp_gun_shop_tut", male.Dictionary);
            Assert.Equal("indicate_right", male.Clip);
            Assert.Equal(male.Dictionary, female.Dictionary);
            Assert.Equal(male.Clip, female.Clip);
        }

        [Fact]
        public void NoneHasNoClip()
        {
            Assert.Null(Style(true).Gesture(ConversationGesture.None));
        }
    }
}
