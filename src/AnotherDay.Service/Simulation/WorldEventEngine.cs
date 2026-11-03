using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Ai;
using AnotherDay.Service.Data;
using AnotherDay.Service.Diagnostics;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Simulation
{
    /// <summary>
    /// Turns observed game events into persistent world facts, updates the
    /// emotions of only the NPCs plausibly close enough to perceive them, and
    /// queues a small number of safe, validated physical reactions.
    /// </summary>
    public sealed class WorldEventEngine
    {
        private readonly AppConfig _config;
        private readonly Database _db;
        private readonly NpcRegistry _registry;
        private readonly EmotionEngine _emotions;
        private readonly Metrics _metrics;
        private readonly SocialEngine _social;
        private readonly ConcurrentQueue<PendingCommand> _pending = new ConcurrentQueue<PendingCommand>();

        public WorldEventEngine(AppConfig config, Database db, NpcRegistry registry, EmotionEngine emotions,
            Metrics metrics, SocialEngine social)
        {
            _config = config;
            _db = db;
            _registry = registry;
            _emotions = emotions;
            _metrics = metrics;
            _social = social;
        }

        public async Task<WorldEventsResponse> IngestAsync(WorldEventsRequest request, CancellationToken ct)
        {
            var response = new WorldEventsResponse();
            if (request == null || request.Events == null || request.Events.Count == 0) return response;

            List<WorldEventRecord> recent = await _db.GetRecentEventsAsync(DateTime.UtcNow.AddSeconds(-12), 30).ConfigureAwait(false);
            var accepted = new List<WorldEventRecord>();

            foreach (WorldEventObservation observation in request.Events.Take(20))
            {
                ct.ThrowIfCancellationRequested();
                if (observation == null || !WorldEventTypes.IsValid(observation.Type)) continue;
                if (IsDuplicate(observation, recent)) continue;

                var record = new WorldEventRecord
                {
                    EventType = observation.Type.ToLowerInvariant(),
                    Position = observation.Position ?? new Vec3(),
                    Radius = Math.Clamp(observation.Radius <= 0 ? 25f : observation.Radius, 1f, 250f),
                    Magnitude = Math.Clamp(observation.Magnitude <= 0 ? 1f : observation.Magnitude, 0f, 10f),
                    Description = Truncate(observation.Description, 240),
                    Zone = Truncate(observation.Zone ?? request.PlayerZone ?? _registry.CurrentZone, 80),
                    CreatedUtc = DateTime.UtcNow
                };

                await _db.AddWorldEventAsync(record).ConfigureAwait(false);
                accepted.Add(record);
                _metrics.Increment("world.events");

                await ApplyReactionsAsync(record, observation, request, ct).ConfigureAwait(false);
                _social.NoteEvent(record);
            }

            response.Accepted = accepted.Count;
            response.Commands = DrainCommands();
            return response;
        }

        public List<ActionCommandDto> DrainCommands()
        {
            var list = new List<ActionCommandDto>();
            DateTime now = DateTime.UtcNow;
            while (_pending.TryDequeue(out PendingCommand pending))
            {
                if (pending.ExpiresUtc < now) continue;
                list.Add(pending.Command);
                if (list.Count >= 8) break;
            }
            return list;
        }

        private async Task ApplyReactionsAsync(WorldEventRecord evt, WorldEventObservation observation,
            WorldEventsRequest request, CancellationToken ct)
        {
            if (!_config.UserSettings.MasterEnabled) return;

            double radius = Math.Max(_config.Simulation.WorldReactionRadiusMeters, evt.Radius * 1.8);
            List<ActiveNpc> witnesses = _registry.Active
                .Where(n => n.Position != null && n.Position.DistanceTo(evt.Position) <= radius)
                .OrderBy(n => n.Position.DistanceTo(evt.Position))
                .Take(10)
                .ToList();

            int sourceHandle = observation.SourceHandle > 0
                ? observation.SourceHandle
                : observation.TargetHandle;

            foreach (ActiveNpc npc in witnesses)
            {
                ct.ThrowIfCancellationRequested();
                npc.Emotion ??= _emotions.Baseline(npc.Profile);
                _emotions.ApplyWorldEvent(npc.Emotion, npc.Profile, evt, npc.Position.DistanceTo(evt.Position));
                await _db.SaveEmotionAsync(npc.Profile.NpcId, npc.Emotion).ConfigureAwait(false);

                ActionCommandDto command = ChooseReaction(npc, evt, sourceHandle);
                if (command != null) Queue(command);
            }
        }

        private ActionCommandDto ChooseReaction(ActiveNpc npc, WorldEventRecord evt, int sourceHandle)
        {
            string action = null;
            int priority = 3;
            int duration = 6000;
            int? target = sourceHandle > 0 ? sourceHandle : (int?)null;

            switch (evt.EventType)
            {
                case WorldEventTypes.Gunshot:
                case WorldEventTypes.Explosion:
                case WorldEventTypes.Threat:
                case WorldEventTypes.PlayerAiming:
                    if (_emotions.ShouldFlee(npc.Profile, npc.Emotion))
                    {
                        action = ActionTypes.Flee;
                        priority = 5;
                        duration = 10000;
                    }
                    else if (_emotions.ShouldConfront(npc.Profile, npc.Emotion) && npc.Profile.Category != NpcCategories.Civilian)
                    {
                        action = ActionTypes.Face;
                        priority = 4;
                    }
                    break;

                case WorldEventTypes.PoliceActivity:
                    if (npc.Profile.Category == NpcCategories.Gang)
                    {
                        action = ActionTypes.WalkAway;
                        priority = 3;
                        duration = 8000;
                        target = null;
                    }
                    else if (npc.Profile.Category == NpcCategories.Police)
                    {
                        action = ActionTypes.Approach;
                        priority = 3;
                    }
                    break;

                case WorldEventTypes.Fight:
                    if (npc.Profile.Category == NpcCategories.Police)
                    {
                        action = ActionTypes.Approach;
                        priority = 4;
                    }
                    else if (_emotions.ShouldFlee(npc.Profile, npc.Emotion))
                    {
                        action = ActionTypes.Flee;
                        priority = 4;
                    }
                    break;

                case WorldEventTypes.Injury:
                case WorldEventTypes.Death:
                    if (npc.Profile.Category == NpcCategories.Paramedic)
                    {
                        action = ActionTypes.Approach;
                        priority = 4;
                    }
                    break;

                case WorldEventTypes.EmergencyVehicle:
                    if (npc.Profile.Category == NpcCategories.Civilian && npc.Emotion.Fear > 0.6f)
                    {
                        action = ActionTypes.WalkAway;
                        priority = 2;
                    }
                    break;
            }

            if (action == null) return null;
            if (ActionRegistry.RequiresTarget(action) && (!target.HasValue || target.Value <= 0))
            {
                if (action == ActionTypes.Face || action == ActionTypes.Approach) action = null;
            }
            if (action == null) return null;

            return new ActionCommandDto
            {
                NpcId = npc.Profile.NpcId,
                NpcHandle = npc.Handle,
                Action = action,
                TargetHandle = target,
                Reason = "Reacting to " + evt.EventType.Replace('_', ' ') + " nearby.",
                Priority = priority,
                DurationMs = duration
            };
        }

        private void Queue(ActionCommandDto command)
        {
            if (command == null) return;
            if (_pending.Count > 32) _pending.TryDequeue(out _);
            _pending.Enqueue(new PendingCommand { Command = command, ExpiresUtc = DateTime.UtcNow.AddSeconds(20) });
        }

        private static bool IsDuplicate(WorldEventObservation observation, List<WorldEventRecord> recent)
        {
            foreach (WorldEventRecord record in recent)
            {
                if (!string.Equals(record.EventType, observation.Type, StringComparison.OrdinalIgnoreCase)) continue;
                double distance = record.Position?.DistanceTo(observation.Position) ?? 999;
                if (distance <= 6) return true;
            }
            return false;
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max);

        private sealed class PendingCommand
        {
            public ActionCommandDto Command { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
    }
}
