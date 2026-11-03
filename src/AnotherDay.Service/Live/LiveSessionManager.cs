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

namespace AnotherDay.Service.Live
{
    /// <summary>
    /// Owns live spoken conversations. Sessions are expensive and Live allows a
    /// single voice per session, so only NPCs the player is actually talking to
    /// (or a nearby pair in an audible exchange) get one; everyone else stays on
    /// the lightweight snapshot simulation.
    /// </summary>
    public sealed class LiveSessionManager : IDisposable
    {
        private readonly AppConfig _config;
        private readonly GeminiClient _gemini;
        private readonly VoiceEngine _voices;
        private readonly NpcRegistry _registry;
        private readonly MemoryEngine _memory;
        private readonly Database _db;
        private readonly Metrics _metrics;
        private readonly ConcurrentDictionary<string, LiveSession> _sessions =
            new ConcurrentDictionary<string, LiveSession>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _acquireGate = new SemaphoreSlim(1, 1);

        public LiveSessionManager(AppConfig config, GeminiClient gemini, VoiceEngine voices, NpcRegistry registry,
            MemoryEngine memory, Database db, Metrics metrics)
        {
            _config = config;
            _gemini = gemini;
            _voices = voices;
            _registry = registry;
            _memory = memory;
            _db = db;
            _metrics = metrics;
        }

        /// <summary>False when live voice cannot work at all; the reason is user-facing.</summary>
        public bool Available => _config.Live.Enabled && _gemini.IsConfigured && !_gemini.AuthRejected;

        public string UnavailableReason
        {
            get
            {
                if (!_config.Live.Enabled) return "live voice disabled in config";
                if (!_gemini.IsConfigured) return "no Gemini API key";
                if (_gemini.AuthRejected) return "Gemini rejected the API key";
                return null;
            }
        }

        public int ActiveCount => _sessions.Count;

        public LiveSession Find(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return null;
            return _sessions.TryGetValue(npcId, out LiveSession session) ? session : null;
        }

        public bool IsActive(string npcId) => Find(npcId) != null;

        public async Task<LiveSession> AcquireAsync(ActiveNpc npc, bool playerArmed, bool playerAiming,
            bool npcInVehicle, bool npcIsDriver, string npcVehicleModel, CancellationToken ct)
        {
            if (npc == null || npc.Profile == null) throw new ArgumentNullException(nameof(npc));
            if (!Available) throw new LiveApiException("Live voice is not available: " + UnavailableReason);

            string npcId = npc.Profile.NpcId;
            if (_sessions.TryGetValue(npcId, out LiveSession existing))
            {
                if (existing.IsConnected) return existing;
                await ReleaseAsync(npcId, "stale").ConfigureAwait(false);
            }

            await _acquireGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sessions.TryGetValue(npcId, out existing) && existing.IsConnected) return existing;
                await EvictIfNeededAsync(npcId).ConfigureAwait(false);

                // The player has one microphone, so only one NPC may hold a live
                // session. Any other session is closed before this one opens, which
                // is what stops a pedestrian the player already left from
                // answering into the new conversation.
                foreach (string other in _sessions.Keys.Where(k => !string.Equals(k, npcId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    Log.Info("Closing live session for " + other + " because a new conversation started.");
                    await ReleaseAsync(other, "another conversation started").ConfigureAwait(false);
                }

                VoiceAssignment voice = await _voices.GetOrAssignAsync(npc.Profile, npc.Emotion).ConfigureAwait(false);
                if (!VoiceEngine.IsCompatible(voice, npc.Profile.IsMale))
                {
                    Log.Warn("Voice " + (voice == null ? "none" : voice.VoiceId) + " does not fit " +
                             npc.Profile.DisplayName + "; assigning a compatible one.");
                    voice = await _voices.ReassignAsync(npc.Profile, npc.Emotion, "incompatible with character model")
                        .ConfigureAwait(false);
                }
                string voiceName = GeminiVoices.Resolve(voice?.VoiceId, npc.Profile.IsMale);
                string persona = await BuildPersonaAsync(npc, playerArmed, playerAiming,
                    npcInVehicle, npcIsDriver, npcVehicleModel).ConfigureAwait(false);

                string apiKey = _gemini.ApiKeyForLive;
                var session = new LiveSession(
                    _config,
                    (handle, token) => ConnectClientAsync(apiKey, voiceName, persona, handle, token),
                    npcId,
                    npc.Profile.DisplayName,
                    voiceName,
                    _config.Live.Model);

                session.TurnCommitted += (playerText, npcText) => _ = PersistTurnAsync(npcId, playerText, npcText);
                session.Closed += reason =>
                {
                    Log.Info("Live session closed for " + npc.Profile.DisplayName + ": " + reason);
                    _sessions.TryRemove(npcId, out _);
                };

                await session.StartAsync(ct).ConfigureAwait(false);
                _sessions[npcId] = session;
                npc.IsTalking = true;
                _metrics.Increment("live.sessions");
                Log.Info("Live voice session opened for " + npc.Profile.DisplayName + " (" + voiceName + ")");
                return session;
            }
            finally { _acquireGate.Release(); }
        }

        private async Task<GeminiLiveClient> ConnectClientAsync(string apiKey, string voice, string persona,
            string handle, CancellationToken ct)
        {
            var client = new GeminiLiveClient(
                _config,
                () => new WebSocketLiveTransport(),
                apiKey,
                _config.Live.Model,
                voice,
                persona,
                handle);
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return client;
        }

        private async Task<string> BuildPersonaAsync(ActiveNpc npc, bool playerArmed, bool playerAiming,
            bool npcInVehicle, bool npcIsDriver, string npcVehicleModel)
        {
            NpcProfile profile = npc.Profile;
            EmotionState emotion = npc.Emotion ?? await _db.GetEmotionAsync(profile.NpcId).ConfigureAwait(false) ?? new EmotionState();
            Relationship relationship = await _db.GetRelationshipAsync(profile.NpcId, "player").ConfigureAwait(false);

            List<MemoryItem> memories;
            try
            {
                memories = (await _memory.RetrieveAsync(profile, _registry.CurrentZone ?? "the neighborhood",
                    Math.Max(4, _config.UserSettings.MemoryTopK)).ConfigureAwait(false)).Take(8).ToList();
            }
            catch (Exception ex)
            {
                Log.Debug("Live persona memory lookup failed: " + ex.Message);
                memories = new List<MemoryItem>();
            }

            List<ConversationTurn> turns;
            try { turns = await _db.GetTurnsForNpcAsync(profile.NpcId, 6).ConfigureAwait(false); }
            catch { turns = new List<ConversationTurn>(); }

            return LivePersona.Build(profile, emotion, relationship, memories, turns,
                _registry.CurrentZone, _registry.CurrentWeather, _registry.ClockHour,
                playerArmed, playerAiming, npcInVehicle, npcIsDriver, npcVehicleModel);
        }

        private async Task PersistTurnAsync(string npcId, string playerText, string npcText)
        {
            try
            {
                string conversationId = "live-" + npcId;
                if (!string.IsNullOrWhiteSpace(playerText))
                {
                    await _db.AddConversationTurnAsync(new ConversationTurn
                    {
                        ConversationId = conversationId,
                        NpcId = npcId,
                        Speaker = "player",
                        Text = playerText,
                        CreatedUtc = DateTime.UtcNow
                    }).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(npcText))
                {
                    await _db.AddConversationTurnAsync(new ConversationTurn
                    {
                        ConversationId = conversationId,
                        NpcId = npcId,
                        Speaker = npcId,
                        Text = npcText,
                        CreatedUtc = DateTime.UtcNow
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Live turn persistence failed: " + ex.Message);
            }
        }

        private async Task EvictIfNeededAsync(string incomingNpcId)
        {
            int limit = Math.Max(1, _config.Live.MaxConcurrentSessions);
            var survivors = _sessions.Where(p => !string.Equals(p.Key, incomingNpcId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .OrderByDescending(s => s.IdleSeconds)
                .ToList();
            if (survivors.Count < limit) return;

            foreach (LiveSession victim in survivors)
            {
                if (_sessions.Count < limit) break;
                Log.Info("Closing idle live session for " + victim.DisplayName + " to stay within the session limit.");
                await ReleaseAsync(victim.NpcId, "evicted").ConfigureAwait(false);
            }
        }

        public async Task ReleaseAsync(string npcId, string reason)
        {
            if (string.IsNullOrEmpty(npcId)) return;
            if (!_sessions.TryRemove(npcId, out LiveSession session)) return;
            try { await session.StopAsync(reason).ConfigureAwait(false); } catch { }
            session.Dispose();
            if (_registry.GetById(npcId) is ActiveNpc npc) npc.IsTalking = false;
            _metrics.Increment("live.sessions.closed");
        }

        public async Task ReleaseAllAsync(string reason)
        {
            foreach (string npcId in _sessions.Keys.ToList())
                await ReleaseAsync(npcId, reason).ConfigureAwait(false);
        }

        public void Dispose()
        {
            foreach (LiveSession session in _sessions.Values)
            {
                try { session.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }

}
