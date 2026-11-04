using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Data;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    public sealed class ActiveNpc
    {
        public int Handle { get; set; }
        public NpcProfile Profile { get; set; }
        public EmotionState Emotion { get; set; }
        public Relationship PlayerRelationship { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public string Fingerprint { get; set; }
        public int Tier { get; set; } = 3;
        public float DistanceToPlayer { get; set; }
        public Vec3 Position { get; set; }
        public bool IsTalking { get; set; }
        public DateTime LastPersistedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSocialUtc { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Maps volatile GTA entity handles onto persistent NPC identities. Entity
    /// handles are never treated as stable: every reuse is checked against the
    /// appearance fingerprint, and weak positional evidence can never be the
    /// sole reason for inheriting another pedestrian's memories.
    /// </summary>
    public sealed class NpcRegistry
    {
        private readonly Database _db;
        private readonly IdentityGenerator _generator;
        private readonly EmotionEngine _emotions;
        private readonly VoiceEngine _voices;
        private readonly AppConfig _config;
        private readonly ConcurrentDictionary<int, ActiveNpc> _byHandle = new ConcurrentDictionary<int, ActiveNpc>();
        private readonly ConcurrentDictionary<string, ActiveNpc> _byId = new ConcurrentDictionary<string, ActiveNpc>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _fingerprintCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _snapshotGate = new SemaphoreSlim(1, 1);
        private DateTime _lastDormantSweep = DateTime.MinValue;
        private DateTime _lastPositionPersist = DateTime.MinValue;

        public NpcRegistry(Database db, IdentityGenerator generator, EmotionEngine emotions, VoiceEngine voices, AppConfig config)
        {
            _db = db;
            _generator = generator;
            _emotions = emotions;
            _voices = voices;
            _config = config;
        }

        public string CurrentZone { get; private set; } = "";
        public string CurrentWeather { get; private set; } = "";
        public int ClockHour { get; private set; } = 12;
        public Vec3 PlayerPosition { get; private set; } = new Vec3();
        public IReadOnlyCollection<ActiveNpc> Active => _byId.Values.ToList();

        public bool TryGet(int handle, out ActiveNpc npc)
        {
            bool ok = _byHandle.TryGetValue(handle, out npc);
            return ok && npc != null;
        }

        public ActiveNpc GetById(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return null;
            _byId.TryGetValue(npcId, out ActiveNpc npc);
            return npc;
        }

        public IReadOnlyList<ActiveNpc> GetTier(int maxTier)
        {
            return _byId.Values.Where(n => n.Tier <= maxTier).OrderBy(n => n.DistanceToPlayer).ToList();
        }

        public async Task<PedSnapshotResponse> ProcessSnapshotAsync(PedSnapshotRequest request, CancellationToken ct)
        {
            var response = new PedSnapshotResponse();
            if (request == null) return response;

            await _snapshotGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                PlayerPosition = request.PlayerPosition ?? PlayerPosition;
                CurrentZone = string.IsNullOrWhiteSpace(request.Zone) ? CurrentZone : request.Zone;
                CurrentWeather = string.IsNullOrWhiteSpace(request.Weather) ? CurrentWeather : request.Weather;
                ClockHour = request.ClockHour;
                if (string.IsNullOrEmpty(CurrentWeather)) CurrentWeather = "clear";

                int limit = Math.Min(request.Peds?.Count ?? 0, Math.Min(_config.Simulation.MaxPedsPerSnapshot, Math.Max(4, _config.UserSettings.SimulationDensity)));
                var seenHandles = new HashSet<int>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < limit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    PedObservation ped = request.Peds[i];
                    if (ped == null) continue;
                    seenHandles.Add(ped.Handle);

                    ActiveNpc active = null;
                    if (_byHandle.TryGetValue(ped.Handle, out ActiveNpc existing))
                    {
                        if (string.Equals(existing.Fingerprint, ped.Fingerprint, StringComparison.Ordinal) ||
                            (string.IsNullOrEmpty(ped.Fingerprint) && existing.Profile.ModelHash == ped.ModelHash))
                        {
                            active = existing;
                        }
                        else
                        {
                            // The handle was recycled by the game. Never carry the
                            // previous ped's identity onto the new entity.
                            _byHandle.TryRemove(ped.Handle, out _);
                            _byId.TryRemove(existing.Profile.NpcId, out _);
                        }
                    }

                    if (active == null)
                        active = await AdoptOrCreateAsync(ped, request.Zone, ct).ConfigureAwait(false);

                    if (active == null) continue;

                    active.Handle = ped.Handle;
                    active.Position = ped.Position;
                    active.DistanceToPlayer = ped.DistanceToPlayer > 0
                        ? ped.DistanceToPlayer
                        : (float)(ped.Position?.DistanceTo(PlayerPosition) ?? 999);
                    active.LastSeenUtc = DateTime.UtcNow;
                    active.Tier = active.DistanceToPlayer <= 8 ? 1 : (active.DistanceToPlayer <= 25 ? 2 : 3);
                    active.Profile.LastSeenUtc = DateTime.UtcNow;

                    // The live model is the authority on sex. A wrong
                    // classification is how a woman ends up wearing a man's
                    // voice, so correcting it re-checks the voice assignment
                    // without touching memories, relationships or personality.
                    if (active.Profile.IsMale != ped.IsMale)
                    {
                        Log.Info("Correcting " + active.Profile.DisplayName + " from " +
                                 (active.Profile.IsMale ? "male" : "female") + " to " +
                                 (ped.IsMale ? "male" : "female") + " from the live model.");
                        active.Profile.IsMale = ped.IsMale;
                        active.Profile.Voice = await _voices.GetOrAssignAsync(active.Profile, active.Emotion).ConfigureAwait(false);
                    }
                    if (NpcRolePolicy.ReconcileObservedRole(active.Profile, ped.CategoryHint))
                    {
                        Log.Info("Correcting unsupported identity details for " + active.Profile.DisplayName +
                            " from the live ped role.");
                        active.Profile.Voice = await _voices.GetOrAssignAsync(active.Profile, active.Emotion).ConfigureAwait(false);
                        await _db.SaveProfileAsync(active.Profile).ConfigureAwait(false);
                    }
                    active.Profile.LastKnownPosition = ped.Position ?? active.Profile.LastKnownPosition;
                    active.Profile.IsDormant = false;
                    _byHandle[ped.Handle] = active;
                    _byId[active.Profile.NpcId] = active;
                    seenIds.Add(active.Profile.NpcId);

                    response.Bindings.Add(new PedBinding
                    {
                        Handle = ped.Handle,
                        NpcId = active.Profile.NpcId,
                        DisplayName = active.Profile.DisplayName,
                        Category = active.Profile.Category,
                        Occupation = active.Profile.Occupation,
                        IsNew = active.Profile.TimesSeen <= 1 && (DateTime.UtcNow - active.Profile.CreatedUtc).TotalSeconds < 10,
                        Confidence = active.Profile.IdentityConfidence,
                        VoiceName = active.Profile.Voice?.VoiceName,
                        EmotionSummary = active.Emotion?.Summary(),
                        IsKnown = active.Profile.TimesSeen > 1
                    });
                }

                // Drop entities the game no longer reports. Handles are never
                // trusted after a short absence, and the fingerprint occupancy
                // count is released so a returning ped can be recognised safely.
                foreach (KeyValuePair<int, ActiveNpc> pair in _byHandle.ToList())
                {
                    if (seenHandles.Contains(pair.Key)) continue;
                    ActiveNpc gone = pair.Value;
                    gone.IsTalking = false;
                    if ((DateTime.UtcNow - gone.LastSeenUtc).TotalSeconds < 20) continue;
                    if (_byHandle.TryRemove(pair.Key, out _))
                    {
                        _byId.TryRemove(gone.Profile.NpcId, out _);
                        ReleaseFingerprint(gone.Fingerprint);
                        gone.Profile.IsDormant = true;
                    }
                }

                if ((DateTime.UtcNow - _lastPositionPersist).TotalSeconds > 45)
                {
                    _lastPositionPersist = DateTime.UtcNow;
                    foreach (ActiveNpc npc in _byId.Values.ToList())
                        await _db.SaveProfileAsync(npc.Profile).ConfigureAwait(false);
                }

                if ((DateTime.UtcNow - _lastDormantSweep).TotalSeconds > 120)
                {
                    _lastDormantSweep = DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        try { await _db.MarkDormantAsync(seenIds).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Warn("Dormant sweep failed: " + ex.Message); }
                    }, CancellationToken.None);
                }
            }
            finally
            {
                _snapshotGate.Release();
            }

            response.KnownNpcCount = await _db.CountProfilesAsync().ConfigureAwait(false);
            return response;
        }

        private async Task<ActiveNpc> AdoptOrCreateAsync(PedObservation ped, string zone, CancellationToken ct)
        {
            NpcProfile profile = null;
            double confidence = 0;
            bool adopted = false;

            // 1. Strong evidence: exact appearance fingerprint.
            if (!string.IsNullOrEmpty(ped.Fingerprint))
            {
                _fingerprintCounts.TryGetValue(ped.Fingerprint, out int activeCount);
                if (activeCount == 0)
                {
                    NpcProfile byFingerprint = await _db.FindByFingerprintAsync(ped.Fingerprint).ConfigureAwait(false);
                    if (byFingerprint != null)
                    {
                        // If the same appearance is already represented by a live
                        // handle, this is a second, distinct pedestrian.
                        if (!_byId.ContainsKey(byFingerprint.NpcId))
                        {
                            profile = byFingerprint;
                            confidence = 0.97;
                            adopted = true;
                        }
                    }
                }
            }

            bool duplicateAppearanceActive = !string.IsNullOrEmpty(ped.Fingerprint) &&
                _fingerprintCounts.TryGetValue(ped.Fingerprint, out int dupCount) && dupCount > 0;

            // 2. Weak evidence: same model, recent, very close. Confidence is
            // deliberately capped below the strong-match threshold. This path
            // is skipped if an identical-looking ped is already represented.
            if (profile == null && !string.IsNullOrEmpty(ped.Fingerprint) && !duplicateAppearanceActive)
            {
                List<NpcProfile> candidates = await _db.GetCandidatesAsync(ped.ModelHash, DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
                NpcProfile best = null;
                double bestScore = 0;
                foreach (NpcProfile c in candidates)
                {
                    if (_byId.ContainsKey(c.NpcId)) continue;
                    double distance = c.LastKnownPosition?.DistanceTo(ped.Position) ?? 999;
                    double ageMinutes = (DateTime.UtcNow - c.LastSeenUtc).TotalMinutes;
                    double score = 0;
                    if (distance <= 4) score += 0.34;
                    else if (distance <= 12) score += 0.26;
                    else if (distance <= 30) score += 0.12;
                    if (ageMinutes <= 5) score += 0.2;
                    else if (ageMinutes <= 20) score += 0.1;
                    if (string.Equals(c.ModelName, ped.ModelName, StringComparison.OrdinalIgnoreCase)) score += 0.15;
                    if (ped.MaxHealth > 0 && c.ModelHash == ped.ModelHash) score += 0.05;
                    if (string.Equals(c.Neighborhood, zone, StringComparison.OrdinalIgnoreCase)) score += 0.05;
                    if (distance <= 12 && score > bestScore) { best = c; bestScore = score; }
                }
                if (best != null && bestScore >= 0.7)
                {
                    profile = best;
                    confidence = Math.Min(0.78, bestScore);
                    adopted = true;
                    Log.Info("Adopted " + profile.DisplayName + " by positional evidence (confidence " + confidence.ToString("0.00") + ")");
                }
            }

            string npcId;
            if (profile != null)
            {
                npcId = profile.NpcId;
                profile.TimesSeen++;
                profile.LastSeenUtc = DateTime.UtcNow;
                profile.LastKnownPosition = ped.Position ?? profile.LastKnownPosition;
                profile.IdentityConfidence = confidence;
                profile.IsDormant = false;
                if (string.IsNullOrEmpty(profile.Voice?.VoiceId))
                    profile.Voice = await _voices.GetOrAssignAsync(profile, null).ConfigureAwait(false);
                await _db.SaveProfileAsync(profile).ConfigureAwait(false);
            }
            else
            {
                npcId = IdentityGenerator.MakeNpcId(ped.Fingerprint);
                int suffix = 1;
                while (await _db.GetProfileAsync(npcId).ConfigureAwait(false) != null || _byId.ContainsKey(npcId))
                {
                    suffix++;
                    npcId = IdentityGenerator.MakeNpcId(ped.Fingerprint) + "_" + suffix;
                    if (suffix > 50) break;
                }

                profile = _generator.Generate(ped, zone, "", npcId);
                EmotionState baseline = _emotions.Baseline(profile);
                await _db.SaveEmotionAsync(npcId, baseline).ConfigureAwait(false);
                profile.Voice = await _voices.GetOrAssignAsync(profile, baseline).ConfigureAwait(false);
                await _db.SaveProfileAsync(profile).ConfigureAwait(false);
                confidence = 1.0;
                Log.Info("New identity " + profile.DisplayName + " (" + profile.Category + ", " + profile.Occupation + ") id=" + npcId);
            }

            EmotionState emotion = await _db.GetEmotionAsync(npcId).ConfigureAwait(false);
            if (emotion == null) emotion = _emotions.Baseline(profile);
            else _emotions.Decay(emotion, profile, DateTime.UtcNow, _config.Simulation.EmotionDecayPerHour);

            Relationship relationship = await _db.GetRelationshipAsync(npcId, "player").ConfigureAwait(false);
            if (relationship == null) relationship = new Relationship { NpcId = npcId, TargetId = "player" };
            if (string.IsNullOrEmpty(relationship.Status)) relationship.Status = "stranger";
            relationship.Familiarity = Math.Max(relationship.Familiarity, Math.Min(1f, profile.TimesSeen * 0.04f));

            var active = new ActiveNpc
            {
                Handle = ped.Handle,
                Profile = profile,
                Emotion = emotion,
                PlayerRelationship = relationship,
                Fingerprint = ped.Fingerprint,
                Position = ped.Position,
                DistanceToPlayer = ped.DistanceToPlayer,
                Tier = ped.DistanceToPlayer <= 8 ? 1 : (ped.DistanceToPlayer <= 25 ? 2 : 3),
                LastSeenUtc = DateTime.UtcNow
            };

            _byHandle[ped.Handle] = active;
            _byId[npcId] = active;
            if (!string.IsNullOrEmpty(ped.Fingerprint))
                _fingerprintCounts.AddOrUpdate(ped.Fingerprint, 1, (_, v) => v + 1);

            if (adopted)
            {
                await _db.SaveEmotionAsync(npcId, emotion).ConfigureAwait(false);
                await _db.SaveRelationshipAsync(relationship).ConfigureAwait(false);
            }
            return active;
        }

        public async Task PersistAfterConversationAsync(ActiveNpc npc)
        {
            if (npc == null) return;
            npc.LastPersistedUtc = DateTime.UtcNow;
            await _db.SaveProfileAsync(npc.Profile).ConfigureAwait(false);
            await _db.SaveEmotionAsync(npc.Profile.NpcId, npc.Emotion).ConfigureAwait(false);
            await _db.SaveRelationshipAsync(npc.PlayerRelationship).ConfigureAwait(false);
        }

        public async Task<Relationship> GetRelationshipAsync(string npcId, string targetId)
        {
            return await _db.GetRelationshipAsync(npcId, targetId).ConfigureAwait(false);
        }

        public async Task SaveRelationshipAsync(Relationship relationship)
        {
            await _db.SaveRelationshipAsync(relationship).ConfigureAwait(false);
        }

        public async Task<List<NpcProfile>> GetKnownNpcsAsync(int limit)
        {
            return await _db.GetRecentProfilesAsync(DateTime.MinValue.AddDays(1), limit).ConfigureAwait(false);
        }

        private void ReleaseFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return;
            _fingerprintCounts.AddOrUpdate(fingerprint, 0, (_, v) => Math.Max(0, v - 1));
        }

        public int CountActive => _byId.Count;
    }
}
