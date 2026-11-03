using System;

namespace AnotherDay.Shared
{
    /// <summary>What the conversation looks like right now.</summary>
    public enum ConversationAnimationPhase
    {
        Idle,
        Listening,
        PlayerSpeaking,
        NpcSpeaking,
        Ending
    }

    /// <summary>
    /// Gestures the plugin can play. The names map to upper-body clips in the
    /// vanilla `gestures@m@standing@casual` / `gestures@f@standing@casual`
    /// dictionaries, so no custom animation pack is needed.
    /// </summary>
    public enum ConversationGesture
    {
        None,
        Nod,
        Explain,
        Shrug,
        Disbelief,
        Point,
        HandsTogether,
        Whatever,
        BringItOn,
        Dismissive
    }

    public enum ConversationExpression
    {
        Neutral,
        Warm,
        Angry,
        Nervous,
        Surprised,
        Confident
    }

    /// <summary>
    /// Personality and mood as the service sends them to the plugin. Animation
    /// choices must come from the same traits that shape the NPC's voice, or
    /// every pedestrian would gesture identically.
    /// </summary>
    public sealed class AnimationStyleDto
    {
        public bool IsMale { get; set; } = true;
        public float Friendliness { get; set; } = 0.5f;
        public float Aggression { get; set; } = 0.5f;
        public float Confidence { get; set; } = 0.5f;
        public float Nervousness { get; set; } = 0.3f;
        public float Humor { get; set; } = 0.5f;
        public string Expression { get; set; }
    }

    /// <summary>Personality + mood translated into concrete animation choices.</summary>
    public sealed class ConversationStyle
    {
        public bool IsMale { get; set; } = true;
        public float Friendliness { get; set; } = 0.5f;
        public float Aggression { get; set; } = 0.5f;
        public float Confidence { get; set; } = 0.5f;
        public float Nervousness { get; set; } = 0.3f;
        public float Humor { get; set; } = 0.5f;
        public ConversationExpression Expression { get; set; } = ConversationExpression.Neutral;

        public static ConversationStyle Neutral
        {
            get { return new ConversationStyle(); }
        }

        /// <summary>Restrained people gesture less and hold still more often.</summary>
        public bool IsRestrained
        {
            get { return Nervousness > 0.6f || Confidence < 0.3f; }
        }

        /// <summary>Vanilla gesture dictionary for this ped's sex.</summary>
        public string GestureDictionary
        {
            get { return IsMale ? "gestures@m@standing@casual" : "gestures@f@standing@casual"; }
        }

        /// <summary>
        /// One playable gesture: the animation dictionary and the clip name.
        /// Both come from verified vanilla content, because a clip that does not
        /// exist plays nothing and a whole gesture pass can silently disappear.
        /// </summary>
        public GestureClip Gesture(ConversationGesture gesture)
        {
            string casual = GestureDictionary;
            switch (gesture)
            {
                case ConversationGesture.Nod:
                    return Clip(casual, IsMale ? "gesture_hello" : "gesture_bye_hard");
                case ConversationGesture.Explain:
                    return Clip(casual, IsMale ? "gesture_hand_left" : "gesture_hand_down");
                case ConversationGesture.Shrug:
                    return Clip(casual, "gesture_shrug_hard");
                case ConversationGesture.Disbelief:
                    return Clip(casual, IsMale ? "gesture_no_way" : "gesture_displeased");
                case ConversationGesture.Point:
                    // Indicate_right exists for both models, unlike the casual
                    // gesture sets which differ by sex.
                    return Clip("mp_gun_shop_tut", "indicate_right");
                case ConversationGesture.HandsTogether:
                    return Clip(casual, IsMale ? "gesture_hand_left" : "gesture_hand_down");
                case ConversationGesture.Whatever:
                    return Clip(casual, IsMale ? "gesture_damn" : "gesture_shrug_hard");
                case ConversationGesture.BringItOn:
                    return Clip(casual, IsMale ? "gesture_bring_it_on" : "gesture_point");
                case ConversationGesture.Dismissive:
                    return Clip(casual, IsMale ? "gesture_no_way" : "gesture_displeased");
                default:
                    return null;
            }
        }

        private static GestureClip Clip(string dictionary, string clip)
        {
            return new GestureClip { Dictionary = dictionary, Clip = clip };
        }

        /// <summary>Vanilla facial clipset variations, checked before use.</summary>
        public string FacialClipset(ConversationExpression expression)
        {
            string sex = IsMale ? "gen_male" : "gen_female";
            string variation;
            switch (expression)
            {
                case ConversationExpression.Warm: variation = "happy"; break;
                case ConversationExpression.Angry: variation = "angry"; break;
                case ConversationExpression.Nervous: variation = "sad"; break;
                case ConversationExpression.Surprised: variation = "happy"; break;
                case ConversationExpression.Confident: variation = "normal"; break;
                default: variation = "normal"; break;
            }
            return "facials@" + sex + "@variations@" + variation;
        }

        public string BaseFacialClipset
        {
            get { return "facials@" + (IsMale ? "gen_male" : "gen_female") + "@base"; }
        }

        public static ConversationStyle FromDto(AnimationStyleDto dto)
        {
            if (dto == null) return Neutral;
            var style = new ConversationStyle
            {
                IsMale = dto.IsMale,
                Friendliness = Clamp01(dto.Friendliness),
                Aggression = Clamp01(dto.Aggression),
                Confidence = Clamp01(dto.Confidence),
                Nervousness = Clamp01(dto.Nervousness),
                Humor = Clamp01(dto.Humor)
            };
            style.Expression = ParseExpression(dto.Expression);
            return style;
        }

        public static ConversationExpression ParseExpression(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ConversationExpression.Neutral;
            switch (value.Trim().ToLowerInvariant())
            {
                case "warm":
                case "happy":
                case "friendly": return ConversationExpression.Warm;
                case "angry":
                case "pissed": return ConversationExpression.Angry;
                case "nervous":
                case "worried":
                case "afraid": return ConversationExpression.Nervous;
                case "surprised":
                case "shocked": return ConversationExpression.Surprised;
                case "confident":
                case "smug": return ConversationExpression.Confident;
                default: return ConversationExpression.Neutral;
            }
        }

        internal static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }

    public sealed class AnimationSettings
    {
        /// <summary>PCM RMS above which the mouth opens. Native audio is quiet.</summary>
        public float MouthLevel { get; set; } = 0.012f;

        public double PlayerMouthHoldSeconds { get; set; } = 0.35;
        public double GestureMinSeconds { get; set; } = 2.4;
        public double GestureMaxSeconds { get; set; } = 6.0;
        public double NodMinSeconds { get; set; } = 4.0;
        public double NodMaxSeconds { get; set; } = 10.0;
        public double ExpressionSeconds { get; set; } = 6.0;
    }

    /// <summary>A dictionary/clip pair that <c>TASK_PLAY_ANIM</c> can play.</summary>
    public sealed class GestureClip
    {
        public string Dictionary { get; set; }
        public string Clip { get; set; }
    }

    /// <summary>One frame of animation intent, produced on the game thread.</summary>
    public sealed class AnimationDecision
    {
        public ConversationAnimationPhase Phase { get; set; }
        public bool MouthOpen { get; set; }
        public bool PlayerMouthOpen { get; set; }
        public ConversationGesture Gesture { get; set; }
        public ConversationExpression Expression { get; set; }
        public bool ExpressionChanged { get; set; }
    }

    /// <summary>
    /// Turns "is audio coming out of the speakers?" into mouth movement and
    /// "what kind of person is this?" into gestures. Free of game code so the
    /// timing rules are testable without GTA V.
    ///
    /// The mouth is driven by actual playback level: it opens only while audio
    /// is arriving and the chunk is loud enough, and closes as soon as the
    /// stream goes quiet, which is also what makes an interruption look right.
    /// </summary>
    public sealed class ConversationAnimationState
    {
        private readonly AnimationSettings _settings;
        private readonly Random _random;
        private ConversationStyle _style = ConversationStyle.Neutral;

        private DateTime _nextGestureUtc = DateTime.MinValue;
        private DateTime _nextNodUtc = DateTime.MinValue;
        private ConversationExpression _expression = ConversationExpression.Neutral;

        public ConversationAnimationState(AnimationSettings settings = null, int seed = 0)
        {
            _settings = settings ?? new AnimationSettings();
            _random = new Random(seed == 0 ? Environment.TickCount : seed);
        }

        public AnimationSettings Settings { get { return _settings; } }
        public ConversationAnimationPhase Phase { get; private set; }
        public bool MouthOpen { get; private set; }
        public bool PlayerMouthOpen { get; private set; }
        public ConversationExpression Expression { get { return _expression; } }
        public ConversationGesture LastGesture { get; private set; }

        public void SetStyle(ConversationStyle style)
        {
            _style = style ?? ConversationStyle.Neutral;
            _expression = ResolveExpression(_style);
        }

        /// <summary>
        /// Advances the state for one game tick.
        /// </summary>
        /// <param name="nowUtc">Current time.</param>
        /// <param name="npcLevel">RMS of the PCM chunk that just arrived (0 when none).</param>
        /// <param name="npcAudio">True while NPC audio is actually being played.</param>
        /// <param name="playerSpeaking">True while the microphone hears the player.</param>
        public AnimationDecision Tick(DateTime nowUtc, float npcLevel, bool npcAudio, bool playerSpeaking)
        {
            var decision = new AnimationDecision { Expression = _expression };

            if (Phase == ConversationAnimationPhase.Ending)
            {
                MouthOpen = false;
                PlayerMouthOpen = false;
                decision.Phase = Phase;
                return decision;
            }

            if (_style == null) _style = ConversationStyle.Neutral;

            MouthOpen = npcAudio && npcLevel >= _settings.MouthLevel;
            PlayerMouthOpen = playerSpeaking;

            Phase = MouthOpen
                ? ConversationAnimationPhase.NpcSpeaking
                : PlayerMouthOpen
                    ? ConversationAnimationPhase.PlayerSpeaking
                    : ConversationAnimationPhase.Listening;

            if (MouthOpen)
            {
                // Talking people still gesture, but not constantly: a gesture
                // every few seconds reads as conversational, one per beat reads
                // as a wind-up toy.
                if (nowUtc >= _nextGestureUtc)
                {
                    ConversationGesture gesture = PickGesture();
                    if (gesture != ConversationGesture.None)
                    {
                        decision.Gesture = gesture;
                        LastGesture = gesture;
                    }
                    _nextGestureUtc = nowUtc.AddSeconds(NextGestureDelay());
                }
            }
            else if (Phase == ConversationAnimationPhase.Listening)
            {
                // Nodding while the other person talks is the single strongest
                // "I am listening" cue, and it costs nothing in a vehicle.
                if (nowUtc >= _nextNodUtc)
                {
                    decision.Gesture = ConversationGesture.Nod;
                    LastGesture = ConversationGesture.Nod;
                    _nextNodUtc = nowUtc.AddSeconds(NextNodDelay());
                }
            }

            ConversationExpression expression = ResolveExpression(_style);
            if (expression != _expression)
            {
                _expression = expression;
                decision.ExpressionChanged = true;
            }

            decision.Phase = Phase;
            decision.MouthOpen = MouthOpen;
            decision.PlayerMouthOpen = PlayerMouthOpen;
            decision.Expression = _expression;
            return decision;
        }

        /// <summary>A single emotional reaction (a joke landing, a threat).</summary>
        public void React(ConversationAnimationPhase unusedPhase, ConversationExpression reaction)
        {
            _expression = reaction;
        }

        public void End(DateTime nowUtc)
        {
            Phase = ConversationAnimationPhase.Ending;
            MouthOpen = false;
            PlayerMouthOpen = false;
            _nextGestureUtc = DateTime.MaxValue;
            _nextNodUtc = DateTime.MaxValue;
        }

        public void Reset(DateTime nowUtc)
        {
            Phase = ConversationAnimationPhase.Idle;
            MouthOpen = false;
            PlayerMouthOpen = false;
            LastGesture = ConversationGesture.None;
            _expression = ResolveExpression(_style);
            _nextGestureUtc = nowUtc;
            _nextNodUtc = nowUtc.AddSeconds(2.0);
        }

        private double NextGestureDelay()
        {
            double min = _settings.GestureMinSeconds;
            double max = Math.Max(min, _settings.GestureMaxSeconds);
            if (_style.IsRestrained)
            {
                min *= 1.4;
                max *= 1.8;
            }
            if (_style.Aggression > 0.7f) max *= 0.8;
            return min + (_random.NextDouble() * (max - min));
        }

        private double NextNodDelay()
        {
            double min = _settings.NodMinSeconds;
            double max = Math.Max(min, _settings.NodMaxSeconds);
            if (_style.IsRestrained) min *= 1.3;
            return min + (_random.NextDouble() * (max - min));
        }

        private ConversationGesture PickGesture()
        {
            ConversationGesture[] pool = GesturesFor(_style);
            if (pool.Length == 0) return ConversationGesture.None;
            return pool[_random.Next(pool.Length)];
        }

        /// <summary>
        /// The gesture vocabulary of a personality. Friendly people explain and
        /// open their hands; aggressive people point and dismiss; nervous people
        /// keep it small; funny people shrug and play dumb.
        /// </summary>
        public static ConversationGesture[] GesturesFor(ConversationStyle style)
        {
            if (style == null) return new[] { ConversationGesture.Explain, ConversationGesture.Shrug };

            if (style.Aggression > 0.7f)
            {
                return new[]
                {
                    ConversationGesture.Point,
                    ConversationGesture.Dismissive,
                    ConversationGesture.BringItOn,
                    ConversationGesture.Explain
                };
            }

            if (style.IsRestrained)
            {
                return new[]
                {
                    ConversationGesture.Shrug,
                    ConversationGesture.HandsTogether,
                    ConversationGesture.Explain
                };
            }

            if (style.Humor > 0.7f && style.Friendliness > 0.5f)
            {
                return new[]
                {
                    ConversationGesture.Whatever,
                    ConversationGesture.Shrug,
                    ConversationGesture.Disbelief,
                    ConversationGesture.Explain
                };
            }

            if (style.Friendliness > 0.65f)
            {
                return new[]
                {
                    ConversationGesture.Explain,
                    ConversationGesture.HandsTogether,
                    ConversationGesture.Whatever,
                    ConversationGesture.Explain
                };
            }

            if (style.Confidence > 0.7f)
            {
                return new[]
                {
                    ConversationGesture.Explain,
                    ConversationGesture.Point,
                    ConversationGesture.Whatever
                };
            }

            return new[]
            {
                ConversationGesture.Explain,
                ConversationGesture.Shrug,
                ConversationGesture.Disbelief
            };
        }

        /// <summary>
        /// Expression from personality when the service has not sent an explicit
        /// mood, so a nervous ped looks nervous even before anything happens.
        /// </summary>
        public static ConversationExpression ResolveExpression(ConversationStyle style)
        {
            if (style == null) return ConversationExpression.Neutral;
            if (style.Expression != ConversationExpression.Neutral) return style.Expression;
            if (style.Aggression > 0.7f) return ConversationExpression.Angry;
            if (style.Nervousness > 0.65f) return ConversationExpression.Nervous;
            if (style.Friendliness > 0.65f) return ConversationExpression.Warm;
            if (style.Confidence > 0.7f) return ConversationExpression.Confident;
            return ConversationExpression.Neutral;
        }
    }

    /// <summary>
    /// Which peds may be animated at all. Every check exists because a
    /// pedestrian animation played on a driving, falling or dead ped is either
    /// invisible or actively harmful — the reason the old code could break a
    /// vehicle.
    /// </summary>
    public static class AnimationSafety
    {
        /// <summary>Facial animation is safe in a vehicle; it never touches control.</summary>
        public static bool CanAnimateFace(bool dead, bool ragdolled)
        {
            return !dead && !ragdolled;
        }

        /// <summary>Body/gesture animation requires a standing, idle person.</summary>
        public static bool CanAnimateBody(bool dead, bool ragdolled, bool inVehicle,
            bool enteringOrExitingVehicle, bool falling, bool swimming, bool inCombat, bool jumping)
        {
            if (dead || ragdolled) return false;
            if (inVehicle || enteringOrExitingVehicle) return false;
            if (falling || swimming || jumping) return false;
            if (inCombat) return false;
            return true;
        }

        /// <summary>The player never receives conversation body animation.</summary>
        public static bool CanAnimatePlayerBody(bool dead, bool inVehicle, bool falling, bool swimming, bool inCombat)
        {
            return !dead && !inVehicle && !falling && !swimming && !inCombat;
        }
    }

    /// <summary>
    /// Entering a vehicle during a conversation ends it. That is a deliberate
    /// choice, not a fallback: the interaction is a pedestrian conversation, the
    /// distance will change immediately, and ending it is the only way to
    /// guarantee no pedestrian task or facial override outlives the transition.
    /// Conversations may still be *started* from a car or a bicycle.
    /// </summary>
    public static class VehicleTransitionPolicy
    {
        public static bool EndsConversationOnEntry(bool conversationActive, bool wasInVehicle, bool isInVehicle)
        {
            return conversationActive && !wasInVehicle && isInVehicle;
        }

        /// <summary>Starting a conversation while already seated is allowed.</summary>
        public static bool CanStartWhileInVehicle(bool inVehicle)
        {
            return true;
        }
    }
}
