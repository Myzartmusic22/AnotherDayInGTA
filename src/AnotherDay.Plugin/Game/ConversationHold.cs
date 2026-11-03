using System;
using AnotherDay.Shared;
using GTA;
using GTA.Math;
using GTA.Native;

namespace AnotherDay.Plugin.Game
{
    /// <summary>
    /// Turns an NPC toward the player for the length of a conversation and hands
    /// it straight back afterwards.
    ///
    /// The three natives are issued on different clocks on purpose. The old
    /// version re-issued all of them on every hold tick (once a second), which
    /// meant <c>TASK_STAND_STILL</c> kept cancelling the turn that had just been
    /// started one call earlier: the pedestrian stopped, faced wherever they
    /// were when they stopped, and only ever drifted towards the player by luck.
    /// Now:
    ///   * stand still - a long finite task, refreshed well before it lapses, so
    ///     the pedestrian stops naturally instead of freezing as a mission ped;
    ///   * head look - refreshed on its own shorter clock, so eye contact
    ///     survives the body turn and the conversation itself;
    ///   * body turn - issued only when the pedestrian is actually not facing the
    ///     player, because re-issuing it every tick restarts the turn and they
    ///     never finish rotating.
    ///
    /// Gestures are played as upper-body secondary tasks by
    /// <see cref="ConversationAnimator"/>; none of these primaries cancel them.
    /// Nobody in a vehicle is touched at all.
    /// </summary>
    internal sealed class ConversationHold
    {
        /// <summary>
        /// Long enough that the task never lapses between refreshes, short
        /// enough that a missed release self-heals within a few seconds.
        /// </summary>
        internal const int StandStillMilliseconds = 8000;

        /// <summary>Re-issued at half the task length so it never runs out.</summary>
        internal const int StandStillRefreshSeconds = 4;

        internal const int LookMilliseconds = 4000;
        internal const int LookRefreshSeconds = 2;

        /// <summary>The turn itself is short; only its trigger is throttled.</summary>
        internal const int TurnMilliseconds = 2500;
        internal const int TurnRefreshSeconds = 1;

        /// <summary>Wider than the look cone: beyond this the body is turned.</summary>
        internal const float FacingToleranceDegrees = 35f;

        private Ped _ped;
        private int _handle;
        private DateTime _nextStandStillUtc = DateTime.MinValue;
        private DateTime _nextLookUtc = DateTime.MinValue;
        private DateTime _nextTurnUtc = DateTime.MinValue;

        // A ped that already belonged to a script is never given a wander task
        // we invented; clearing its own tasks is all we are allowed to do.
        private bool _wasMissionEntity;

        public bool IsAttached
        {
            get { return Alive() != null; }
        }

        public void Attach(Ped ped)
        {
            if (!GameThread.IsCurrent)
            {
                PluginLog.Warn("Refusing to hold a pedestrian from a background thread.");
                return;
            }

            Release();
            if (ped == null || !ped.Exists() || ped.IsDead) return;

            _ped = ped;
            _handle = ped.Handle;
            _nextStandStillUtc = DateTime.MinValue;
            _nextLookUtc = DateTime.MinValue;
            _nextTurnUtc = DateTime.MinValue;

            try { _wasMissionEntity = Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, ped); }
            catch { _wasMissionEntity = false; }

            try
            {
                Ped character = PlayerPed();
                if (character != null)
                {
                    DateTime now = DateTime.UtcNow;
                    ApplyStandStill(ped, now);
                    ApplyLook(ped, character, now);
                    ApplyTurn(ped, character, now);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Initial conversation hold failed: " + ex.Message);
            }
        }

        /// <summary>Re-applies the hold the engagement state asked for this tick.</summary>
        public void Apply(EngagementHold hold)
        {
            if (!GameThread.IsCurrent) return;

            Ped ped = Alive();
            if (ped == null || hold == EngagementHold.None) return;

            // Someone in a vehicle is left completely alone. Issuing driving,
            // braking or look tasks to a driver is exactly the kind of
            // interference that stopped traffic behaving normally.
            if (hold == EngagementHold.StayInVehicle) return;
            if (ped.IsInVehicle()) return;

            Ped character = PlayerPed();
            if (character == null) return;

            try
            {
                DateTime now = DateTime.UtcNow;
                ApplyStandStill(ped, now);
                ApplyLook(ped, character, now);
                ApplyTurn(ped, character, now);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Conversation hold failed: " + ex.Message);
            }
        }

        /// <summary>Stop naturally, without a mission flag or an event block.</summary>
        private void ApplyStandStill(Ped ped, DateTime now)
        {
            if (now < _nextStandStillUtc) return;
            _nextStandStillUtc = now.AddSeconds(StandStillRefreshSeconds);
            Function.Call(Hash.TASK_STAND_STILL, ped, StandStillMilliseconds);
        }

        /// <summary>Head and eyes track the player while the body settles.</summary>
        private void ApplyLook(Ped ped, Ped player, DateTime now)
        {
            if (now < _nextLookUtc) return;
            _nextLookUtc = now.AddSeconds(LookRefreshSeconds);
            Function.Call(Hash.TASK_LOOK_AT_ENTITY, ped, player, LookMilliseconds, 0, 2);
        }

        /// <summary>
        /// The body turn is only restarted when the pedestrian is not facing the
        /// player. Restarting it every tick is why they used to keep turning
        /// without arriving.
        /// </summary>
        private void ApplyTurn(Ped ped, Ped player, DateTime now)
        {
            if (now < _nextTurnUtc) return;
            _nextTurnUtc = now.AddSeconds(TurnRefreshSeconds);

            bool facing;
            try { facing = Function.Call<bool>(Hash.IS_PED_FACING_PED, ped, player, FacingToleranceDegrees); }
            catch { facing = false; }
            if (facing) return;

            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, ped, player, TurnMilliseconds);
        }

        /// <summary>
        /// Hands the ped back to the street. No mission flag to undo, no event
        /// blocking to undo, and no vehicle task: the only thing that was ever
        /// taken away is the walk, so the only thing given back is the walk.
        /// </summary>
        public void Release()
        {
            if (!GameThread.IsCurrent)
            {
                PluginLog.Debug("Skipping a hold release that came from a background thread.");
                _ped = null;
                _handle = 0;
                _wasMissionEntity = false;
                return;
            }

            Ped ped = Alive();
            if (ped != null)
            {
                try
                {
                    // Somebody in a car keeps their seat and their driving task,
                    // and so keeps the ordinary ability to get out when the game
                    // decides to.
                    if (!ped.IsInVehicle())
                    {
                        Function.Call(Hash.CLEAR_PED_TASKS, ped);
                        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, ped);

                        // Clearing the tasks alone leaves a pedestrian standing
                        // where the conversation ended, which reads as frozen.
                        // A wander task hands back the ordinary street life.
                        if (!_wasMissionEntity)
                            Function.Call(Hash.TASK_WANDER_STANDARD, ped, 10f, 10);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Debug("Releasing the conversation hold failed: " + ex.Message);
                }
            }

            _ped = null;
            _handle = 0;
            _wasMissionEntity = false;
            _nextStandStillUtc = DateTime.MinValue;
            _nextLookUtc = DateTime.MinValue;
            _nextTurnUtc = DateTime.MinValue;
        }

        private static Ped PlayerPed()
        {
            try
            {
                GTA.Player player = GTA.Game.Player;
                Ped ped = player == null ? null : player.Character;
                return ped != null && ped.Exists() ? ped : null;
            }
            catch
            {
                return null;
            }
        }

        private Ped Alive()
        {
            if (_ped == null && _handle == 0) return null;
            try
            {
                Ped ped = _handle != 0 ? Entity.FromHandle(_handle) as Ped : _ped;
                if (ped == null || !ped.Exists() || ped.IsDead) return null;
                return ped;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Bicycles and trains have no scripted driving task that tolerates car
        /// natives; those are never given one.
        /// </summary>
        internal static bool IsUnsupportedVehicle(Vehicle vehicle)
        {
            if (vehicle == null) return true;
            try
            {
                int vehicleClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS, vehicle);
                return vehicleClass == 13 || vehicleClass == 21; // cycles, trains
            }
            catch
            {
                return true;
            }
        }

        internal static bool IsDriving(Ped ped, Vehicle vehicle)
        {
            if (ped == null || vehicle == null) return false;
            try { return Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1) == ped.Handle; }
            catch { return false; }
        }
    }
}
