using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Data;
using AnotherDay.Service.Diagnostics;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Ai
{
    /// <summary>
    /// Orchestrates one conversation turn: retrieval, prompting, validation,
    /// persistence and voice synthesis. Never blocks the game thread and never
    /// lets a failed AI call break gameplay.
    /// </summary>
    public sealed class ConversationEngine
    {
        private readonly AppConfig _config;
        private readonly AppPaths _paths;
        private readonly Database _db;
        private readonly NpcRegistry _registry;
        private readonly MemoryEngine _memory;
        private readonly EmotionEngine _emotions;
        private readonly AudioService _audio;
        private readonly ActionRegistry _actions;
        private readonly Metrics _metrics;
        private readonly GeminiProvider _gemini;
        private readonly OfflineProvider _offline;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _speechInFlight =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        public ConversationEngine(AppConfig config, AppPaths paths, Database db, NpcRegistry registry,
            MemoryEngine memory, EmotionEngine emotions, AudioService audio, ActionRegistry actions,
            Metrics metrics, GeminiProvider gemini, OfflineProvider offline)
        {
            _config = config;
            _paths = paths;
            _db = db;
            _registry = registry;
            _memory = memory;
            _emotions = emotions;
            _audio = audio;
            _actions = actions;
            _metrics = metrics;
            _gemini = gemini;
            _offline = offline;
        }

        /// <summary>
        /// A key is present and Google has not rejected it. A stored key that
        /// returns 401 must read as offline, not online.
        /// </summary>
        public bool AiConfigured => _gemini.IsConfigured && !_gemini.AuthRejected;

        public async Task<ConversationResponse> ConverseAsync(ConversationRequest request, CancellationToken ct)
        {
            if (!_config.UserSettings.MasterEnabled)
                return Fail("The mod is currently disabled.");

            if (request == null || string.IsNullOrWhiteSpace(request.PlayerText))
                return Fail("Say something first.");

            string text = request.PlayerText.Trim();
            if (text.Length > 700) text = text.Substring(0, 700);

            if (!_registry.TryGet(request.NpcHandle, out ActiveNpc npc) || npc == null)
                return Fail("That person is no longer nearby.");

            return await ConverseInternalAsync(request, npc, text, null, ct).ConfigureAwait(false);
        }

        public async Task<ConversationResponse> VoiceConverseAsync(VoiceConversationRequest request, CancellationToken ct)
        {
            if (!_config.UserSettings.MasterEnabled)
                return Fail("The mod is currently disabled.");
            if (request == null || string.IsNullOrWhiteSpace(request.AudioPath))
                return Fail("No recording was supplied.");
            if (!AiConfigured || !_config.UserSettings.AiEnabled)
                return Fail(_gemini.AuthRejected
                    ? "Gemini rejected the saved API key, so speech cannot be transcribed. Run AnotherDay.Service.exe --set-key and paste a valid key, then restart the game."
                    : "Voice input needs a configured Gemini API key. Text chat still works offline.");
            if (!IsAllowedRecordingPath(request.AudioPath))
                return Fail("The recording path is not allowed.");

            if (!_registry.TryGet(request.NpcHandle, out ActiveNpc npc) || npc == null)
                return Fail("That person is no longer nearby.");

            string transcript;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(20, _config.Service.AiTimeoutSeconds)));
                transcript = await _gemini.TranscribeAsync(request.AudioPath, timeout.Token).ConfigureAwait(false);
                _metrics.RecordTranscription();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail("Speech recognition timed out.");
            }
            catch (Exception ex)
            {
                Log.Warn("Transcription failed: " + ex.Message);
                _metrics.RecordError("Transcription failed: " + ex.Message);
                return Fail("I couldn't make out the recording. Try again.");
            }

            if (string.IsNullOrWhiteSpace(transcript))
                return Fail("I didn't catch that.");
            transcript = transcript.Trim();
            if (transcript.Length > 700) transcript = transcript.Substring(0, 700);

            var conversation = new ConversationRequest
            {
                NpcHandle = request.NpcHandle,
                PlayerHandle = request.PlayerHandle,
                PlayerText = transcript,
                ConversationId = request.ConversationId,
                Nearby = request.Nearby,
                PlayerPosition = request.PlayerPosition,
                Zone = request.Zone,
                Weather = request.Weather,
                ClockHour = request.ClockHour,
                PlayerArmed = request.PlayerArmed
            };

            ConversationResponse response = await ConverseInternalAsync(conversation, npc, transcript, transcript, ct).ConfigureAwait(false);
            if (response.Ok) response.Transcript = transcript;
            return response;
        }

        private async Task<ConversationResponse> ConverseInternalAsync(ConversationRequest request, ActiveNpc npc,
            string playerText, string transcript, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            NpcProfile profile = npc.Profile;
            string conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
                ? "conv-" + profile.NpcId
                : request.ConversationId.Trim();

            try
            {
                npc.IsTalking = true;
                EmotionState emotion = npc.Emotion ?? _emotions.Baseline(profile);
                Relationship relationship = npc.PlayerRelationship
                    ?? await _db.GetRelationshipAsync(profile.NpcId, "player").ConfigureAwait(false);

                float sentiment = _emotions.AnalyzeSentiment(playerText);
                _emotions.ApplyPlayerUtterance(emotion, profile, relationship, playerText, sentiment);

                List<MemoryItem> memories = await _memory.RetrieveAsync(profile, playerText,
                    Math.Max(1, _config.UserSettings.MemoryTopK)).ConfigureAwait(false);
                List<ConversationTurn> recent = await _db.GetRecentTurnsAsync(conversationId, 12).ConfigureAwait(false);
                List<WorldEventRecord> events = await GetPerceivedEventsAsync(npc).ConfigureAwait(false);

                var nearby = new List<NearbyNpc>();
                var nearbyById = new Dictionary<string, ActiveNpc>(StringComparer.OrdinalIgnoreCase);
                foreach (NearbyPerson person in (request.Nearby ?? new List<NearbyPerson>()).Take(8))
                {
                    if (person == null || string.IsNullOrWhiteSpace(person.NpcId)) continue;
                    ActiveNpc other = _registry.GetById(person.NpcId);
                    Relationship rel = null;
                    if (other != null)
                    {
                        nearbyById[person.NpcId] = other;
                        rel = await _db.GetRelationshipAsync(profile.NpcId, person.NpcId).ConfigureAwait(false);
                    }
                    nearby.Add(new NearbyNpc
                    {
                        NpcId = person.NpcId,
                        DisplayName = person.DisplayName ?? other?.Profile.DisplayName ?? "someone",
                        Category = person.Category ?? other?.Profile.Category ?? "civilian",
                        Distance = person.DistanceToPlayer,
                        RelationshipSummary = rel != null && rel.Familiarity > 0.1f
                            ? string.Format("trust {0:0.0}, affinity {1:0.0}", rel.Trust, rel.Affinity)
                            : null
                    });
                }

                var context = new ConversationContext
                {
                    Profile = profile,
                    Emotion = emotion,
                    PlayerRelationship = relationship,
                    Memories = memories,
                    RecentTurns = recent,
                    Nearby = nearby,
                    RecentEvents = events,
                    PlayerText = playerText,
                    Zone = string.IsNullOrWhiteSpace(request.Zone) ? profile.Neighborhood : request.Zone,
                    Weather = string.IsNullOrWhiteSpace(request.Weather) ? _registry.CurrentWeather : request.Weather,
                    ClockHour = request.ClockHour > 0 ? request.ClockHour : _registry.ClockHour,
                    PlayerArmed = request.PlayerArmed,
                    PlayerAiming = request.PlayerAiming,
                    ConversationId = conversationId,
                    RelationshipSummary = relationship.Describe()
                };

                AiConversationResult result = null;
                bool usedFallback = false;

                if (_gemini.IsConfigured && _config.UserSettings.AiEnabled)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _config.Service.AiTimeoutSeconds)));
                        result = await _gemini.ConverseAsync(context, timeout.Token).ConfigureAwait(false);
                        _metrics.Increment("ai.turn.success");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn("Gemini conversation failed; using local behavior: " + ex.Message);
                        _metrics.Increment("ai.turn.fallback");
                        _metrics.RecordError("Gemini conversation failed: " + ex.Message);
                    }
                }

                if (result == null)
                {
                    result = await _offline.ConverseAsync(context, ct).ConfigureAwait(false);
                    usedFallback = true;
                }

                if (string.IsNullOrWhiteSpace(result.Dialogue))
                    throw new AiUnavailableException("The response contained no dialogue.");

                string reply = SanitizeDialogue(result.Dialogue, 700);

                _emotions.ApplyAiDeltas(emotion, result.EmotionDelta);
                ApplyRelationshipDeltas(relationship, result.RelationshipDelta);
                relationship.Familiarity = Math.Clamp(relationship.Familiarity + 0.05f, 0f, 1f);
                relationship.LastInteractionUtc = DateTime.UtcNow;
                relationship.Status = DescribeStatus(relationship);
                relationship.Clamp();

                profile.LastSeenUtc = DateTime.UtcNow;
                npc.Emotion = emotion;
                npc.PlayerRelationship = relationship;
                npc.Profile.LastSeenUtc = DateTime.UtcNow;

                float memoryImportance = Math.Clamp(0.4f + Math.Abs(sentiment) * 0.25f + (result.WantsToEnd ? 0.05f : 0f), 0.2f, 0.95f);
                await _memory.RecordInteractionAsync(profile, playerText, reply, memoryImportance,
                    sentiment, conversationId).ConfigureAwait(false);

                foreach (AiMemoryProposal proposal in result.Memories.Take(3))
                {
                    string subject = !string.IsNullOrWhiteSpace(proposal.SubjectNpcId) &&
                                     (_registry.GetById(proposal.SubjectNpcId) != null || !string.IsNullOrWhiteSpace(proposal.SubjectNpcId))
                        ? proposal.SubjectNpcId
                        : null;
                    await _memory.AddMemoryAsync(profile, proposal.Kind, proposal.Content, proposal.Importance,
                        proposal.Sentiment, subject, conversationId).ConfigureAwait(false);
                }

                await _db.SaveProfileAsync(profile).ConfigureAwait(false);
                await _db.SaveEmotionAsync(profile.NpcId, emotion).ConfigureAwait(false);
                await _db.SaveRelationshipAsync(relationship).ConfigureAwait(false);

                await _db.AddConversationTurnAsync(new ConversationTurn
                {
                    ConversationId = conversationId,
                    NpcId = profile.NpcId,
                    Speaker = "player",
                    Text = playerText,
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);
                await _db.AddConversationTurnAsync(new ConversationTurn
                {
                    ConversationId = conversationId,
                    NpcId = profile.NpcId,
                    Speaker = "npc",
                    Text = reply,
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                try { await _memory.SummarizeIfNeededAsync(profile, _config.Simulation.MemoryMaxPerNpc).ConfigureAwait(false); }
                catch (Exception ex) { Log.Debug("Memory maintenance failed: " + ex.Message); }

                List<ActionCommandDto> commands = _actions.Validate(result.Actions, npc, request.PlayerHandle, nearbyById);

                AudioResult audio = null;
                if (_config.Voice.Enabled && _config.UserSettings.VoicesEnabled && reply.Length > 0)
                {
                    audio = await SynthesizeSpeechAsync(npc, emotion, reply, ct).ConfigureAwait(false);
                }

                await AdjustPlayerReputationAsync(sentiment).ConfigureAwait(false);

                // Single-line result record so a session can be audited from the log alone.
                Log.Info("Conversation with " + profile.DisplayName + " (" + profile.Category + ", " + profile.NpcId + "): " +
                    (usedFallback || result.UsedFallback ? "offline" : "ai") + ", " + stopwatch.ElapsedMilliseconds + " ms, " +
                    memories.Count + " memory item(s), " + commands.Count + " action(s), voice=" +
                    (audio != null ? audio.VoiceName + (audio.Fallback ? " (fallback)" : "") : "none") + ", reply=\"" +
                    (reply.Length > 120 ? reply.Substring(0, 120) + "..." : reply) + "\"");

                npc.IsTalking = false;
                stopwatch.Stop();
                _metrics.SetActiveConversations(ActiveConversationCount());

                return new ConversationResponse
                {
                    Ok = true,
                    ConversationId = conversationId,
                    NpcId = profile.NpcId,
                    DisplayName = profile.DisplayName,
                    Transcript = transcript,
                    ReplyText = reply,
                    InnerThought = result.InnerThought,
                    VoiceName = profile.Voice?.VoiceName ?? audio?.VoiceName,
                    AudioPath = audio?.Path,
                    AudioFromFallbackVoice = audio?.Fallback ?? false,
                    EmotionSummary = emotion.Summary(),
                    RelationshipSummary = relationship.Describe(),
                    WantsToEnd = result.WantsToEnd,
                    Actions = commands,
                    MemoriesUsed = memories.Take(5).Select(m => MemoryEngine.FormatForPrompt(new List<MemoryItem> { m }).TrimStart('-', ' ')).ToList(),
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    UsedOfflineFallback = usedFallback || result.UsedFallback
                };
            }
            catch (OperationCanceledException)
            {
                return Fail("The conversation was cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("Conversation failed for " + profile.DisplayName + ": " + ex.Message, ex);
                _metrics.RecordError("Conversation failed: " + ex.Message);
                return Fail("Something went wrong while talking to " + profile.DisplayName + ".");
            }
            finally
            {
                npc.IsTalking = false;
            }
        }

        /// <summary>
        /// Generates an autonomous NPC-to-NPC exchange. Used by the social
        /// scheduler; effects are applied here so the caller only handles audio
        /// and display.
        /// </summary>
        public async Task<AiGroupResult> GenerateGroupAsync(List<ActiveNpc> participants, string topic, CancellationToken ct)
        {
            if (participants == null || participants.Count < 2) return null;

            var context = new GroupContext
            {
                Topic = topic,
                Zone = _registry.CurrentZone,
                Weather = _registry.CurrentWeather,
                ClockHour = _registry.ClockHour
            };

            foreach (ActiveNpc npc in participants.Take(3))
            {
                context.Participants.Add(npc.Profile);
                context.Emotions.Add(npc.Emotion ?? _emotions.Baseline(npc.Profile));
                foreach (MemoryItem memory in (await _memory.RetrieveAsync(npc.Profile, topic ?? "neighborhood",
                    Math.Max(2, _config.UserSettings.MemoryTopK / 2)).ConfigureAwait(false)).Take(3))
                {
                    context.Memories.Add(memory);
                }
                foreach (ConversationTurn turn in await _db.GetTurnsForNpcAsync(npc.Profile.NpcId, 3).ConfigureAwait(false))
                    context.RecentTurns.Add(turn);
            }
            context.RecentEvents = await _db.GetRecentEventsAsync(DateTime.UtcNow.AddMinutes(-20), 10).ConfigureAwait(false);

            AiGroupResult result = null;
            if (_gemini.IsConfigured && _config.UserSettings.AiEnabled && _config.Simulation.UseAiForNpcChatter)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _config.Service.AiTimeoutSeconds)));
                    result = await _gemini.ConverseGroupAsync(context, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("Gemini group conversation failed; using local behavior: " + ex.Message);
                }
            }
            if (result == null)
                result = await _offline.ConverseGroupAsync(context, ct).ConfigureAwait(false);

            var byId = participants.GroupBy(p => p.Profile.NpcId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            result.Turns = result.Turns
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text) && byId.ContainsKey(t.SpeakerNpcId ?? ""))
                .Take(4)
                .ToList();

            foreach (AiGroupTurn turn in result.Turns)
            {
                ActiveNpc speaker = byId[turn.SpeakerNpcId];
                _emotions.ApplyAiDeltas(speaker.Emotion, turn.EmotionDelta);
                await _db.SaveEmotionAsync(speaker.Profile.NpcId, speaker.Emotion).ConfigureAwait(false);
                await _db.AddConversationTurnAsync(new ConversationTurn
                {
                    ConversationId = "social-" + string.Join("-", byId.Keys.OrderBy(k => k)),
                    NpcId = speaker.Profile.NpcId,
                    Speaker = speaker.Profile.NpcId,
                    Text = turn.Text,
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);
            }

            foreach (AiMemoryProposal proposal in result.Memories.Take(4))
            {
                foreach (ActiveNpc npc in byId.Values.Take(3))
                {
                    await _memory.AddMemoryAsync(npc.Profile, proposal.Kind, proposal.Content,
                        Math.Min(0.7f, proposal.Importance), proposal.Sentiment, proposal.SubjectNpcId,
                        "social-" + string.Join("-", byId.Keys.OrderBy(k => k))).ConfigureAwait(false);
                }
            }

            return result;
        }

        public async Task<AudioResult> SynthesizeSpeechAsync(ActiveNpc npc, EmotionState emotion, string text, CancellationToken ct)
        {
            string key = npc.Profile.NpcId;
            if (_speechInFlight.TryRemove(key, out CancellationTokenSource previous))
            {
                try { previous.Cancel(); } catch { }
                previous.Dispose();
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _speechInFlight[key] = cts;
            try
            {
                return await _audio.SynthesizeAsync(npc.Profile, emotion, text, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Log.Warn("Speech synthesis failed for " + npc.Profile.DisplayName + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (_speechInFlight.TryGetValue(key, out CancellationTokenSource current) && ReferenceEquals(current, cts))
                    _speechInFlight.TryRemove(key, out _);
                cts.Dispose();
            }
        }

        public void CancelSpeech(string npcId)
        {
            if (!string.IsNullOrEmpty(npcId) && _speechInFlight.TryRemove(npcId, out CancellationTokenSource cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }

        private async Task<List<WorldEventRecord>> GetPerceivedEventsAsync(ActiveNpc npc)
        {
            List<WorldEventRecord> events = await _db.GetRecentEventsAsync(DateTime.UtcNow.AddMinutes(-20), 40).ConfigureAwait(false);
            if (npc.Position == null) return events.Take(6).ToList();
            return events
                .Where(e => e.Position == null ||
                            e.Position.DistanceTo(npc.Position) <= Math.Max(60, e.Radius * 2.5))
                .Take(6)
                .ToList();
        }

        private static void ApplyRelationshipDeltas(Relationship relationship, RelationshipDelta delta)
        {
            if (relationship == null || delta == null) return;
            relationship.Trust = Math.Clamp(relationship.Trust + Math.Clamp(delta.Trust, -0.2f, 0.2f), -1f, 1f);
            relationship.Affinity = Math.Clamp(relationship.Affinity + Math.Clamp(delta.Affinity, -0.2f, 0.2f), -1f, 1f);
            relationship.Fear = Math.Clamp(relationship.Fear + Math.Clamp(delta.Fear, 0f, 0.2f), 0f, 1f);
            relationship.Respect = Math.Clamp(relationship.Respect + Math.Clamp(delta.Respect, -0.2f, 0.2f), -1f, 1f);
        }

        private static string DescribeStatus(Relationship r)
        {
            if (r.Fear > 0.65f) return "intimidated";
            if (r.Affinity > 0.6f && r.Trust > 0.5f) return "friendly";
            if (r.Affinity < -0.5f) return "hostile";
            if (r.Trust < -0.3f) return "distrustful";
            if (r.Familiarity > 0.45f) return "acquaintance";
            return "stranger";
        }

        private async Task AdjustPlayerReputationAsync(float sentiment)
        {
            if (Math.Abs(sentiment) < 0.15f) return;
            try
            {
                double current = await _db.GetPlayerReputationAsync().ConfigureAwait(false);
                await _db.SetPlayerReputationAsync(current + sentiment * 0.8).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Debug("Reputation update failed: " + ex.Message); }
        }

        private int ActiveConversationCount() =>
            _registry.Active.Count(n => n != null && n.IsTalking);

        private bool IsAllowedRecordingPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return false;
                var info = new FileInfo(full);
                if (!info.Exists || info.Length < 512 || info.Length > 20 * 1024 * 1024) return false;

                string temp = Path.GetFullPath(Path.GetTempPath());
                string root = Path.GetFullPath(_paths.Root);
                return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SanitizeDialogue(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
            if (clean.Length > max) clean = clean.Substring(0, max);
            return clean.Trim();
        }

        private static ConversationResponse Fail(string message) => new ConversationResponse { Ok = false, Error = message };
    }
}
