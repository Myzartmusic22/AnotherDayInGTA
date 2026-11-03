using System;
using AnotherDay.Shared;
using GTA;
using GTA.Math;
using GTA.Native;

namespace AnotherDay.Plugin.Game
{
    /// <summary>
    /// Second validation layer. The service already filtered action proposals;
    /// this class re-checks live entity state and only maps the approved action
    /// name onto a fixed native task. No model output reaches a native call.
    /// </summary>
    internal sealed class ActionExecutor
    {
        private readonly Action<string> _notify;

        public ActionExecutor(Action<string> notify)
        {
            _notify = notify;
        }

        /// <summary>
        /// Tells the executor whether a handle is the NPC the player is talking
        /// to right now. A live conversation owns that ped: no wander, flee,
        /// approach or vehicle task may be issued over it.
        /// </summary>
        public Func<int, bool> IsConversationPartner { get; set; }

        public void Execute(ActionCommandDto command)
        {
            if (command == null || !ActionTypes.IsValid(command.Action)) return;
            if (command.NpcHandle <= 0) return;

            Entity entity = Entity.FromHandle(command.NpcHandle);
            Ped ped = entity as Ped;
            if (ped == null || !ped.Exists() || ped.IsDead) return;

            Entity targetEntity = command.TargetHandle.HasValue && command.TargetHandle.Value > 0
                ? Entity.FromHandle(command.TargetHandle.Value)
                : null;
            Ped target = targetEntity as Ped;
            if (target != null && (!target.Exists() || target.IsDead)) target = null;

            if (IsConversationPartner != null && IsConversationPartner(command.NpcHandle)
                && ConflictsWithConversation(command.Action))
            {
                PluginLog.Debug("Skipping " + command.Action + " for " + command.NpcId +
                    ": an active conversation owns this NPC.");
                return;
            }

            if (RequiresTarget(command.Action) && target == null)
            {
                PluginLog.Debug("Skipping " + command.Action + " for " + command.NpcId + ": target unavailable.");
                return;
            }

            if (target != null && ped.Position.DistanceTo(target.Position) > 80)
            {
                PluginLog.Debug("Skipping " + command.Action + ": target too far.");
                return;
            }

            int duration = Math.Min(30000, Math.Max(500, command.DurationMs));
            try
            {
                switch (command.Action)
                {
                    case ActionTypes.Face:
                    case ActionTypes.InitiateConversation:
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, ped, target, duration);
                        break;

                    case ActionTypes.Approach:
                        if (ped.IsInVehicle())
                        {
                            Function.Call(Hash.TASK_LEAVE_VEHICLE, ped, ped.CurrentVehicle, 0);
                            break;
                        }
                        ped.Task.GoStraightTo(target.Position, duration, PedMoveBlendRatio.Walk, 1.2f, 0f);
                        break;

                    case ActionTypes.Follow:
                        Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, ped, target, 0f, 0f, 0f, 2.0f, duration, true);
                        break;

                    case ActionTypes.Flee:
                    case ActionTypes.ReactDanger:
                        Function.Call(Hash.TASK_SMART_FLEE_PED, ped, target, 100f, duration, false, false);
                        break;

                    case ActionTypes.WalkAway:
                    {
                        Vector3 away = ped.Position + (ped.Position - (target?.Position ?? ped.Position)).Normalized * 18f;
                        ped.Task.GoStraightTo(away, duration, PedMoveBlendRatio.Walk, 1.4f, 0f);
                        break;
                    }

                    case ActionTypes.CallForHelp:
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, ped, target, duration);
                        Function.Call(Hash.TASK_USE_MOBILE_PHONE_TIMED, ped, duration);
                        break;

                    case ActionTypes.Surrender:
                        Function.Call(Hash.TASK_HANDS_UP, ped, duration, target, 0, true);
                        break;

                    case ActionTypes.Cooperate:
                    case ActionTypes.Refuse:
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, ped, target, duration);
                        Function.Call(Hash.TASK_STAND_STILL, ped, duration);
                        break;

                    case ActionTypes.LeaveVehicle:
                        if (ped.CurrentVehicle != null)
                            Function.Call(Hash.TASK_LEAVE_VEHICLE, ped, ped.CurrentVehicle, 0);
                        break;

                    case ActionTypes.Stop:
                        Function.Call(Hash.CLEAR_PED_TASKS, ped);
                        break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Action " + command.Action + " failed: " + ex.Message);
                _notify("Action failed: " + command.Action);
            }
        }

        /// <summary>
        /// Tasks that would move the ped away, pull it out of a vehicle or wipe
        /// the hold. Refusals, facial turns and surrenders are still allowed:
        /// they happen in place and are part of the conversation.
        /// </summary>
        private static bool ConflictsWithConversation(string action)
        {
            switch (action)
            {
                case ActionTypes.Approach:
                case ActionTypes.Follow:
                case ActionTypes.WalkAway:
                case ActionTypes.Flee:
                case ActionTypes.ReactDanger:
                case ActionTypes.LeaveVehicle:
                case ActionTypes.Stop:
                    return true;
                default:
                    return false;
            }
        }

        private static bool RequiresTarget(string action)
        {
            switch (action)
            {
                case ActionTypes.Face:
                case ActionTypes.Approach:
                case ActionTypes.Follow:
                case ActionTypes.Flee:
                case ActionTypes.CallForHelp:
                case ActionTypes.ReactDanger:
                    return true;
                default:
                    return false;
            }
        }
    }
}
