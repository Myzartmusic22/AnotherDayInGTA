using System;
using System.Collections.Generic;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Ai
{
    /// <summary>
    /// The model can only request actions from this registry. Nothing generated
    /// by the model is ever executed directly; the plugin performs a second
    /// validation against live game state before running a native task.
    /// </summary>
    public sealed class ActionRegistry
    {
        private readonly NpcRegistry _registry;

        public ActionRegistry(NpcRegistry registry)
        {
            _registry = registry;
        }

        public List<ActionCommandDto> Validate(IEnumerable<AiActionProposal> proposals, ActiveNpc npc,
            int playerHandle, IReadOnlyDictionary<string, ActiveNpc> nearbyById)
        {
            var commands = new List<ActionCommandDto>();
            if (proposals == null || npc == null) return commands;

            foreach (AiActionProposal proposal in proposals)
            {
                if (proposal == null || !ActionTypes.IsValid(proposal.Type)) continue;
                string type = proposal.Type.ToLowerInvariant();
                if (type == ActionTypes.Speak) continue;

                int? targetHandle = null;
                if (!string.IsNullOrWhiteSpace(proposal.Target))
                {
                    string target = proposal.Target.Trim();
                    if (target.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                        target.Equals("the player", StringComparison.OrdinalIgnoreCase))
                    {
                        targetHandle = playerHandle > 0 ? playerHandle : (int?)null;
                    }
                    else if (nearbyById != null && nearbyById.TryGetValue(target, out ActiveNpc other))
                    {
                        targetHandle = other.Handle;
                    }
                    else
                    {
                        ActiveNpc resolved = _registry.GetById(target);
                        if (resolved != null) targetHandle = resolved.Handle;
                    }
                }

                if (RequiresTarget(type) && (!targetHandle.HasValue || targetHandle.Value <= 0))
                {
                    Log.Debug("Rejected action " + type + ": no valid target for " + npc.Profile.DisplayName);
                    continue;
                }

                if (type == ActionTypes.Approach || type == ActionTypes.Follow || type == ActionTypes.Face)
                {
                    double distance = npc.Position?.DistanceTo(_registry.PlayerPosition) ?? 999;
                    if (distance > 60)
                    {
                        Log.Debug("Rejected action " + type + ": target is too far away.");
                        continue;
                    }
                }

                int priority = Math.Clamp(proposal.Priority <= 0 ? 3 : proposal.Priority, 1, 5);
                int duration = Math.Clamp(proposal.DurationMs <= 0 ? 4000 : proposal.DurationMs, 500, 30000);

                commands.Add(new ActionCommandDto
                {
                    NpcId = npc.Profile.NpcId,
                    NpcHandle = npc.Handle,
                    Action = type,
                    TargetHandle = targetHandle,
                    Reason = Truncate(proposal.Reason, 200),
                    Priority = priority,
                    DurationMs = duration
                });
            }

            return commands;
        }

        public static bool RequiresTarget(string type)
        {
            switch (type)
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

        /// <summary>Second-line validation used by tests and the plugin contract.</summary>
        public static bool IsExecutable(ActionCommandDto command)
        {
            if (command == null || !ActionTypes.IsValid(command.Action)) return false;
            if (command.NpcHandle <= 0) return false;
            if (RequiresTarget(command.Action) && (!command.TargetHandle.HasValue || command.TargetHandle.Value <= 0))
                return false;
            if (command.DurationMs < 500 || command.DurationMs > 30000) return false;
            if (command.Priority < 1 || command.Priority > 5) return false;
            return true;
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max);
    }
}
