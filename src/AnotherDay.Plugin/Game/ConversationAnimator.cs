using System;
using AnotherDay.Shared;
using GTA;
using GTA.Native;

namespace AnotherDay.Plugin.Game
{
    /// <summary>
    /// Applies the shared animation state to real peds. Every native call here
    /// runs on the game thread, every handle is revalidated on the tick, and
    /// every animation is guarded by the ped's own state: no body animation on
    /// somebody driving, riding a bicycle, falling, swimming, fighting or
    /// entering a car, and no facial override on a dead or ragdolled ped.
    ///
    /// Lip movement is driven by the RMS of the PCM chunk that just reached the
    /// speakers, so it starts and stops with the audio rather than with a
    /// transcript, and an interruption closes the mouth immediately.
    /// </summary>
    internal sealed class ConversationAnimator
    {
        private const string FacialDictionary = "mp_facial";
        private const string TalkingAnim = "mic_chatter";

        // 16 = upper body only, 32 = secondary task: gestures leave walking,
        // facing and vehicle tasks untouched.
        private const int GestureFlags = 48;

        private ConversationAnimationState _state;
        private ConversationStyle _style = ConversationStyle.Neutral;
        private int _npcHandle;

        private bool _npcMouthOpen;
        private bool _playerMouthOpen;
        private DateTime _npcMouthRefreshUtc = DateTime.MinValue;
        private DateTime _playerMouthRefreshUtc = DateTime.MinValue;
        private bool _expressionApplied;
        private DateTime _nextDictRequestUtc = DateTime.MinValue;
        private DateTime _nextFacialDictRequestUtc = DateTime.MinValue;
        private readonly System.Collections.Generic.HashSet<string> _missingDictionaries =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFailureLogUtc = DateTime.MinValue;

        public bool IsAttached { get { return _npcHandle != 0; } }
        public ConversationAnimationPhase Phase
        {
            get { return _state == null ? ConversationAnimationPhase.Idle : _state.Phase; }
        }

        public void Attach(Ped ped, ConversationStyle style)
        {
            Release();
            if (ped == null || !ped.Exists()) return;

            _npcHandle = ped.Handle;
            _style = style ?? ConversationStyle.Neutral;
            _state = new ConversationAnimationState();
            _state.SetStyle(_style);
            _state.Reset(DateTime.UtcNow);
            _missingDictionaries.Clear();

            // Stream the facial dictionary before the first word. The override
            // is a silent no-op on a dictionary that was never requested, and
            // that alone makes the mouth look completely dead.
            RequestFacialDictionary(DateTime.UtcNow);
        }

        /// <summary>The service just told us who this person really is.</summary>
        public void SetStyle(ConversationStyle style)
        {
            if (style == null) return;
            _style = style;
            if (_state != null) _state.SetStyle(style);
        }

        public void Tick(DateTime nowUtc, float npcLevel, bool npcAudio, bool playerSpeaking)
        {
            if (_state == null) return;
            if (!GameThread.IsCurrent) return;

            Ped npc = Npc();
            if (npc == null)
            {
                _npcHandle = 0;
                return;
            }

            try
            {
                bool dead = npc.IsDead || Function.Call<bool>(Hash.IS_ENTITY_DEAD, npc);
                bool ragdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, npc);
                bool inVehicle = Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, npc, true);
                bool falling = Function.Call<bool>(Hash.IS_PED_FALLING, npc);
                bool jumping = Function.Call<bool>(Hash.IS_PED_JUMPING, npc);
                bool swimming = Function.Call<bool>(Hash.IS_PED_SWIMMING, npc);
                bool melee = Function.Call<bool>(Hash.IS_PED_IN_MELEE_COMBAT, npc);

                bool faceOk = AnimationSafety.CanAnimateFace(dead, ragdoll);
                bool bodyOk = AnimationSafety.CanAnimateBody(dead, ragdoll, inVehicle, inVehicle,
                    falling, swimming, melee, jumping);

                AnimationDecision decision = _state.Tick(nowUtc, npcLevel, npcAudio, playerSpeaking);

                SetMouth(npc, decision.MouthOpen && faceOk, nowUtc,
                    ref _npcMouthOpen, ref _npcMouthRefreshUtc);

                if (decision.ExpressionChanged || !_expressionApplied)
                    ApplyExpression(npc, decision.Expression);

                if (decision.Gesture != ConversationGesture.None && bodyOk)
                    PlayGesture(npc, decision.Gesture, nowUtc);

                Ped player = Player();
                if (player != null)
                {
                    bool playerDead = player.IsDead || Function.Call<bool>(Hash.IS_ENTITY_DEAD, player);
                    bool playerRagdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, player);
                    SetMouth(player, decision.PlayerMouthOpen && AnimationSafety.CanAnimateFace(playerDead, playerRagdoll),
                        nowUtc, ref _playerMouthOpen, ref _playerMouthRefreshUtc);
                }
            }
            catch (Exception ex)
            {
                LogFailure("Conversation animation failed", ex);
            }
        }

        /// <summary>
        /// Ends the animation and hands the face back. Safe to call twice, safe
        /// to call for a ped that has already died or despawned.
        /// </summary>
        public void Release()
        {
            if (GameThread.IsCurrent)
            {
                Ped npc = Npc();
                if (npc != null)
                {
                    try
                    {
                        Function.Call(Hash.CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, npc);
                        if (_expressionApplied)
                            Function.Call(Hash.SET_FACIAL_CLIPSET, npc, _style.BaseFacialClipset);
                    }
                    catch (Exception ex) { LogFailure("Resetting the NPC face failed", ex); }
                }

                Ped player = Player();
                if (player != null && _playerMouthOpen)
                {
                    try { Function.Call(Hash.CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, player); }
                    catch (Exception ex) { LogFailure("Resetting the player face failed", ex); }
                }
            }

            if (_state != null) _state.End(DateTime.UtcNow);

            _npcHandle = 0;
            _npcMouthOpen = false;
            _playerMouthOpen = false;
            _expressionApplied = false;
            _npcMouthRefreshUtc = DateTime.MinValue;
            _playerMouthRefreshUtc = DateTime.MinValue;
            _nextFacialDictRequestUtc = DateTime.MinValue;
        }

        private void SetMouth(Ped ped, bool open, DateTime nowUtc, ref bool applied, ref DateTime refreshUtc)
        {
            try
            {
                if (open)
                {
                    // The dictionary has to be resident or the override silently
                    // does nothing. The working reference implementation
                    // requests it and waits for it instead of applying into an
                    // unloaded dictionary for the whole conversation.
                    if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, FacialDictionary))
                    {
                        if (applied)
                        {
                            Function.Call(Hash.CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, ped);
                            applied = false;
                        }
                        RequestFacialDictionary(nowUtc);
                        return;
                    }

                    // Re-applied periodically: the idle override fades, and a
                    // jaw that freezes mid-sentence looks worse than none. It is
                    // applied twice because the ped's own facial idle can
                    // consume the first one.
                    if (!applied || nowUtc >= refreshUtc)
                    {
                        Function.Call(Hash.SET_FACIAL_IDLE_ANIM_OVERRIDE, ped, TalkingAnim, FacialDictionary);
                        Function.Call(Hash.SET_FACIAL_IDLE_ANIM_OVERRIDE, ped, TalkingAnim, FacialDictionary);
                        refreshUtc = nowUtc.AddMilliseconds(600);
                    }
                    applied = true;
                }
                else if (applied)
                {
                    Function.Call(Hash.CLEAR_FACIAL_IDLE_ANIM_OVERRIDE, ped);
                    applied = false;
                }
            }
            catch (Exception ex)
            {
                applied = false;
                LogFailure("Mouth animation failed", ex);
            }
        }

        /// <summary>
        /// Streams the facial dictionary in the background. An override applied
        /// to a dictionary that was never requested does nothing at all, which
        /// is the difference between a talking face and a dead one.
        /// </summary>
        private void RequestFacialDictionary(DateTime nowUtc)
        {
            if (nowUtc < _nextFacialDictRequestUtc) return;
            _nextFacialDictRequestUtc = nowUtc.AddSeconds(1);
            try { Function.Call(Hash.REQUEST_ANIM_DICT, FacialDictionary); }
            catch (Exception ex) { LogFailure("Requesting the facial dictionary failed", ex); }
        }

        private void ApplyExpression(Ped ped, ConversationExpression expression)
        {
            try
            {
                // SET_FACIAL_CLIPSET is a silent no-op when the set is absent,
                // and this SHVDN build does not expose DOES_ANIM_SET_EXIST, so
                // the vanilla variation is applied directly. A missing set costs
                // an expression, never stability.
                Function.Call(Hash.SET_FACIAL_CLIPSET, ped, _style.FacialClipset(expression));
                _expressionApplied = true;
            }
            catch (Exception ex)
            {
                LogFailure("Facial expression failed", ex);
            }
        }

        private void PlayGesture(Ped ped, ConversationGesture gesture, DateTime nowUtc)
        {
            GestureClip clip = _style == null ? null : _style.Gesture(gesture);
            if (clip == null || string.IsNullOrEmpty(clip.Dictionary) || string.IsNullOrEmpty(clip.Clip)) return;

            string dictionary = clip.Dictionary;
            if (_missingDictionaries.Contains(dictionary)) return;

            try
            {
                if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dictionary))
                {
                    // One missing dictionary does not disable every gesture:
                    // point uses its own set, the others use the casual sets.
                    _missingDictionaries.Add(dictionary);
                    PluginLog.Debug("Gesture dictionary missing on this build: " + dictionary);
                    return;
                }

                // Never block waiting for the dictionary: the game thread must
                // keep ticking, so a gesture is simply skipped until it loads.
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dictionary))
                {
                    if (nowUtc >= _nextDictRequestUtc)
                    {
                        Function.Call(Hash.REQUEST_ANIM_DICT, dictionary);
                        _nextDictRequestUtc = nowUtc.AddMilliseconds(300);
                    }
                    return;
                }

                // 16 = upper body only, 32 = secondary task: the arms gesture
                // while the legs, the facing and any vehicle task stay intact.
                Function.Call(Hash.TASK_PLAY_ANIM, ped, dictionary, clip.Clip, 4f, -8f, -1,
                    GestureFlags, 0f, false, false, false);
            }
            catch (Exception ex)
            {
                LogFailure("Gesture failed", ex);
            }
        }

        private Ped Npc()
        {
            if (_npcHandle == 0) return null;
            try
            {
                Ped ped = Entity.FromHandle(_npcHandle) as Ped;
                if (ped == null || !ped.Exists()) return null;
                return ped;
            }
            catch
            {
                return null;
            }
        }

        private static Ped Player()
        {
            try
            {
                Ped ped = GTA.Game.Player == null ? null : GTA.Game.Player.Character;
                return ped != null && ped.Exists() ? ped : null;
            }
            catch
            {
                return null;
            }
        }

        private void LogFailure(string message, Exception ex)
        {
            if (DateTime.UtcNow < _lastFailureLogUtc) return;
            _lastFailureLogUtc = DateTime.UtcNow.AddSeconds(10);
            PluginLog.Warn(message + ": " + ex.Message);
        }
    }
}
