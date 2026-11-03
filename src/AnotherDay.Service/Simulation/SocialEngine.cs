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
    /// Schedules autonomous NPC-to-NPC conversations. Work is bounded: only a
    /// few nearby, willing pairs are considered, only one group is generated at
    /// a time, and every generated line is delivered to the game through a
    /// pull queue so the render thread never waits on the model.
    /// </summary>
    public sealed class SocialEngine
    {
        private readonly AppConfig _config;
        private readonly Database _db;
        private readonly NpcRegistry _registry;
        private readonly ConversationEngine _conversation;
        private readonly EmotionEngine _emotions;
        private readonly Metrics _metrics;
        private readonly ConcurrentQueue<NpcSpeechEvent> _outbox = new ConcurrentQueue<NpcSpeechEvent>();
        private readonly ConcurrentDictionary<string, SocialConversation> _active =
            new ConcurrentDictionary<string, SocialConversation>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastPairUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(WorldEventRecord Event, DateTime SeenUtc)> _topics =
            new ConcurrentQueue<(WorldEventRecord, DateTime)>();
        private int _ticking;
        private DateTime _lastTickUtc = DateTime.MinValue;

        public SocialEngine(AppConfig config, Database db, NpcRegistry registry, ConversationEngine conversation,
            EmotionEngine emotions, Metrics metrics)
        {
            _config = config;
            _db = db;
            _registry = registry;
            _conversation = conversation;
            _emotions = emotions;
            _metrics = metrics;
        }

        public int ActiveConversationCount => _active.Count;

        public void NoteEvent(WorldEventRecord evt)
        {
            if (evt == null) return;
            if (_topics.Count > 12) _topics.TryDequeue(out _);
            _topics.Enqueue((evt, DateTime.UtcNow));
        }

        public List<NpcSpeechEvent> DrainSpeeches()
        {
            var list = new List<NpcSpeechEvent>();
            while (_outbox.TryDequeue(out NpcSpeechEvent speech))
            {
                list.Add(speech);
                if (list.Count >= 6) break;
            }
            return list;
        }

        public async Task TickAsync(CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
            try
            {
                if (!_config.UserSettings.MasterEnabled || !_config.UserSettings.AutonomousEnabled) return;
                int max = Math.Min(_config.UserSettings.MaxActiveConversations, _config.Simulation.MaxActiveConversations);
                if (max <= 0 || _active.Count >= max) return;

                // Natural pacing: a chatter budget per minute rather than a
                // conversation every tick.
                int perMinute = Math.Max(0, _config.UserSettings.BackgroundChatterPerMinute);
                if (perMinute <= 0) return;
                double secondsSince = (DateTime.UtcNow - _lastTickUtc).TotalSeconds;
                if (secondsSince < Math.Max(2, 60.0 / perMinute)) return;
                _lastTickUtc = DateTime.UtcNow;

                List<ActiveNpc> candidates = _registry.GetTier(2)
                    .Where(n => !n.IsTalking && n.Handle > 0 && n.DistanceToPlayer <= 25 &&
                                (DateTime.UtcNow - n.LastSocialUtc).TotalSeconds > 75)
                    .OrderBy(n => n.DistanceToPlayer)
                    .Take(8)
                    .ToList();
                if (candidates.Count < 2) return;

                (ActiveNpc A, ActiveNpc B, double Score) best = (null, null, 0);
                foreach (ActiveNpc a in candidates)
                {
                    foreach (ActiveNpc b in candidates)
                    {
                        if (ReferenceEquals(a, b) || string.CompareOrdinal(a.Profile.NpcId, b.Profile.NpcId) >= 0) continue;
                        double distance = a.Position?.DistanceTo(b.Position) ?? 999;
                        if (distance > Math.Max(4, _config.Simulation.SocialPairRadiusMeters)) continue;

                        // Some people simply do not want to talk.
                        float willingness = _emotions.WillingnessToTalk(a.Profile, a.Emotion ?? _emotions.Baseline(a.Profile), a.PlayerRelationship);
                        willingness += _emotions.WillingnessToTalk(b.Profile, b.Emotion ?? _emotions.Baseline(b.Profile), b.PlayerRelationship);
                        if (willingness < 0.55f) continue;

                        string pairKey = PairKey(a.Profile.NpcId, b.Profile.NpcId);
                        if (_lastPairUtc.TryGetValue(pairKey, out DateTime last) &&
                            (DateTime.UtcNow - last).TotalSeconds < 240) continue;

                        double score = willingness + (a.Profile.Personality.Sociability + b.Profile.Personality.Sociability) * 0.5
                                       - distance * 0.04 + Random.Shared.NextDouble() * 0.5;
                        if (score > best.Score) best = (a, b, score);
                    }
                }

                if (best.A == null || best.B == null) return;

                var participants = new List<ActiveNpc> { best.A, best.B };
                if (_config.Simulation.AllowThirdPartyJoin && candidates.Count > 2 && Random.Shared.NextDouble() < 0.15)
                {
                    ActiveNpc third = candidates.FirstOrDefault(c => c != best.A && c != best.B &&
                        (c.Position?.DistanceTo(best.A.Position) ?? 999) < 7 &&
                        c.Profile.Personality.Sociability > 0.5f);
                    if (third != null) participants.Add(third);
                }

                await StartConversationAsync(participants, ChooseTopic(participants), false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("Social tick failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        public async Task<TriggerSocialResponse> TriggerAsync(TriggerSocialRequest request, CancellationToken ct)
        {
            if (request == null) return new TriggerSocialResponse { Ok = false, Error = "No request." };
            if (!_config.UserSettings.MasterEnabled)
                return new TriggerSocialResponse { Ok = false, Error = "The mod is disabled." };

            if (!_registry.TryGet(request.NpcHandle, out ActiveNpc first) || first == null)
                return new TriggerSocialResponse { Ok = false, Error = "The first person is not available." };
            if (!_registry.TryGet(request.OtherHandle, out ActiveNpc second) || second == null)
                return new TriggerSocialResponse { Ok = false, Error = "The second person is not available." };
            if (ReferenceEquals(first, second))
                return new TriggerSocialResponse { Ok = false, Error = "They are the same person." };

            double distance = first.Position?.DistanceTo(second.Position) ?? 999;
            if (distance > (request.Force ? 30 : 10))
                return new TriggerSocialResponse { Ok = false, Error = "Those two are too far apart." };

            string pairKey = PairKey(first.Profile.NpcId, second.Profile.NpcId);
            if (_lastPairUtc.TryGetValue(pairKey, out DateTime last) && (DateTime.UtcNow - last).TotalSeconds < 45 && !request.Force)
                return new TriggerSocialResponse { Ok = false, Error = "They just spoke. Give them a moment." };

            await StartConversationAsync(new List<ActiveNpc> { first, second }, ChooseTopic(new List<ActiveNpc> { first, second }), true, ct).ConfigureAwait(false);
            return new TriggerSocialResponse { Ok = true, StartedConversations = 1 };
        }

        private async Task StartConversationAsync(List<ActiveNpc> participants, string topic, bool forced, CancellationToken ct)
        {
            if (participants == null || participants.Count < 2) return;
            var usable = participants.Where(p => p != null && p.Handle > 0 && !p.IsTalking).Take(3).ToList();
            if (usable.Count < 2) return;

            string conversationId = "social-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var conversation = new SocialConversation
            {
                ConversationId = conversationId,
                Topic = topic,
                EndsUtc = DateTime.UtcNow.AddSeconds(90)
            };

            foreach (ActiveNpc npc in usable)
            {
                npc.IsTalking = true;
                npc.LastSocialUtc = DateTime.UtcNow;
                conversation.NpcIds.Add(npc.Profile.NpcId);
                conversation.Handles[npc.Profile.NpcId] = npc.Handle;
            }
            _active[conversationId] = conversation;
            _metrics.SetActiveConversations(_active.Count);

            try
            {
                AiGroupResult result = await _conversation.GenerateGroupAsync(usable, topic, ct).ConfigureAwait(false);
                if (result == null || result.Turns.Count == 0) return;

                foreach (AiGroupTurn turn in result.Turns.Take(4))
                {
                    ActiveNpc speaker = usable.FirstOrDefault(n =>
                        string.Equals(n.Profile.NpcId, turn.SpeakerNpcId, StringComparison.OrdinalIgnoreCase));
                    if (speaker == null) continue;
                    string text = Sanitize(turn.Text);

                    speaker.Emotion ??= _emotions.Baseline(speaker.Profile);
                    AudioResult audio = null;
                    try
                    {
                        audio = await _conversation.SynthesizeSpeechAsync(speaker, speaker.Emotion, text, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log.Debug("Social TTS failed: " + ex.Message); }

                    _outbox.Enqueue(new NpcSpeechEvent
                    {
                        ConversationId = conversationId,
                        NpcId = speaker.Profile.NpcId,
                        NpcHandle = speaker.Handle,
                        DisplayName = speaker.Profile.DisplayName,
                        Text = text,
                        VoiceName = speaker.Profile.Voice?.VoiceName ?? audio?.VoiceName,
                        AudioPath = audio?.Path,
                        AudioFromFallbackVoice = audio?.Fallback ?? false,
                        TargetHandles = usable.Where(u => u != speaker).Select(u => u.Handle).ToList(),
                        DelayMs = 250,
                        IsAutonomous = true
                    });
                }

                foreach (ActiveNpc npc in usable)
                {
                    foreach (ActiveNpc other in usable.Where(o => o != npc))
                    {
                        Relationship rel = await _db.GetRelationshipAsync(npc.Profile.NpcId, other.Profile.NpcId).ConfigureAwait(false);
                        rel.Familiarity = Math.Clamp(rel.Familiarity + (forced ? 0.06f : 0.03f), 0f, 1f);
                        rel.LastInteractionUtc = DateTime.UtcNow;
                        rel.Clamp();
                        await _db.SaveRelationshipAsync(rel).ConfigureAwait(false);
                    }
                }

                _metrics.Increment("social.conversations");
                Log.Info("Social conversation " + conversationId + " between " +
                         string.Join(", ", usable.Select(u => u.Profile.DisplayName)) + " about " + topic);
            }
            finally
            {
                foreach (ActiveNpc npc in usable) npc.IsTalking = false;
                _active.TryRemove(conversationId, out _);
                _metrics.SetActiveConversations(_active.Count);
            }
        }

        private string ChooseTopic(List<ActiveNpc> participants)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-3);
            WorldEventRecord eventTopic = null;
            while (_topics.TryDequeue(out (WorldEventRecord Event, DateTime SeenUtc) item))
            {
                if (item.SeenUtc < cutoff) continue;
                if (item.Event.Position == null || participants.Any(p => p.Position == null ||
                    item.Event.Position.DistanceTo(p.Position) <= 70))
                {
                    eventTopic = item.Event;
                    break;
                }
            }
            if (eventTopic != null)
            {
                string description = string.IsNullOrWhiteSpace(eventTopic.Description)
                    ? eventTopic.EventType.Replace('_', ' ')
                    : eventTopic.Description;
                return "the recent " + eventTopic.EventType.Replace('_', ' ') + " nearby: " + description;
            }

            ActiveNpc lead = participants[0];
            switch (lead.Profile.Category)
            {
                case NpcCategories.Police:
                    return "their shift, the neighborhood, and recent trouble";
                case NpcCategories.Gang:
                    return "turf, rivals, and who has been moving through the block";
                case NpcCategories.Shopkeeper:
                case NpcCategories.Business:
                case NpcCategories.Employee:
                    return "business, customers, and the state of the street";
                case NpcCategories.Paramedic:
                case NpcCategories.Firefighter:
                    return "a long shift and the calls they have been on";
                case NpcCategories.Driver:
                    return "traffic, bad drivers, and getting across the city";
                default:
                    return _registry.ClockHour < 6 ? "how late it is and why they are still out"
                        : _registry.ClockHour > 20 ? "the night, the noise, and the neighborhood"
                        : "the weather, the neighborhood, and whatever is going on";
            }
        }

        private static string PairKey(string a, string b) =>
            string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        private static string Sanitize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            return clean.Length > 400 ? clean.Substring(0, 400) : clean;
        }
    }
}
