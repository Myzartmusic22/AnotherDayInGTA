using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;
using Microsoft.Data.Sqlite;

namespace AnotherDay.Service.Data
{
    /// <summary>
    /// Single-writer SQLite store. All access is serialized through a semaphore
    /// so the game-facing service never sees SQLITE_BUSY. Failure to open the
    /// database is non-fatal: the service falls back to in-memory operation.
    /// </summary>
    public sealed class Database : IAsyncDisposable
    {
        private readonly AppPaths _paths;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private SqliteConnection _connection;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public bool IsAvailable { get; private set; }
        public string LastError { get; private set; }
        public string DatabasePath => _paths.DatabasePath;

        public Database(AppPaths paths)
        {
            _paths = paths;
        }

        public async Task InitializeAsync()
        {
            _paths.EnsureCreated();
            try
            {
                BackupExistingDatabase();
                _connection = new SqliteConnection("Data Source=" + _paths.DatabasePath + ";Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
                await _connection.OpenAsync().ConfigureAwait(false);
                await ExecuteAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);
                await ExecuteAsync("PRAGMA synchronous=NORMAL;").ConfigureAwait(false);
                await ExecuteAsync("PRAGMA foreign_keys=ON;").ConfigureAwait(false);
                await ExecuteAsync("PRAGMA busy_timeout=5000;").ConfigureAwait(false);
                await MigrateAsync().ConfigureAwait(false);
                await SeedVoicesAsync().ConfigureAwait(false);
                IsAvailable = true;
                Log.Info("Database ready at " + _paths.DatabasePath);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsAvailable = false;
                Log.Error("Database unavailable; running in memory-only mode: " + ex.Message, ex);
                QuarantineCorruptDatabase();
            }
        }

        private void BackupExistingDatabase()
        {
            try
            {
                if (!File.Exists(_paths.DatabasePath)) return;
                var info = new FileInfo(_paths.DatabasePath);
                if (info.Length == 0) return;
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string target = Path.Combine(_paths.BackupDirectory, "anotherday-" + stamp + ".db");
                File.Copy(_paths.DatabasePath, target, false);
                foreach (string old in Directory.GetFiles(_paths.BackupDirectory, "anotherday-*.db")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Skip(7))
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Database backup failed: " + ex.Message);
            }
        }

        private void QuarantineCorruptDatabase()
        {
            try
            {
                if (File.Exists(_paths.DatabasePath))
                {
                    string bad = _paths.DatabasePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Move(_paths.DatabasePath, bad, true);
                    Log.Warn("Moved unreadable database to " + bad);
                }
                if (_connection != null) { _connection.Dispose(); _connection = null; }
                _connection = new SqliteConnection("Data Source=" + _paths.DatabasePath + ";Mode=ReadWriteCreate;Pooling=False");
                _connection.Open();
                ExecuteAsync("PRAGMA journal_mode=WAL;").GetAwaiter().GetResult();
                MigrateAsync().GetAwaiter().GetResult();
                SeedVoicesAsync().GetAwaiter().GetResult();
                IsAvailable = true;
                LastError = null;
                Log.Warn("Created a fresh database after quarantine.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not recreate database: " + ex.Message);
            }
        }

        private async Task MigrateAsync()
        {
            await ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  applied_utc TEXT NOT NULL
);").ConfigureAwait(false);

            long current = Convert.ToInt64(await ScalarAsync("SELECT COALESCE(MAX(version),0) FROM schema_migrations;").ConfigureAwait(false));

            if (current < 1)
            {
                await ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS npc_profiles (
  npc_id TEXT PRIMARY KEY,
  fingerprint TEXT NOT NULL,
  model_hash INTEGER NOT NULL,
  model_name TEXT NOT NULL,
  is_male INTEGER NOT NULL DEFAULT 1,
  display_name TEXT NOT NULL,
  age INTEGER NOT NULL,
  occupation TEXT NOT NULL,
  category TEXT NOT NULL,
  neighborhood TEXT NOT NULL,
  background TEXT NOT NULL,
  personality_json TEXT NOT NULL,
  personality_desc TEXT NOT NULL,
  speech_json TEXT NOT NULL,
  interests_json TEXT NOT NULL,
  beliefs_json TEXT NOT NULL,
  quirks_json TEXT NOT NULL,
  voice_json TEXT,
  created_utc TEXT NOT NULL,
  last_seen_utc TEXT NOT NULL,
  last_x REAL NOT NULL DEFAULT 0,
  last_y REAL NOT NULL DEFAULT 0,
  last_z REAL NOT NULL DEFAULT 0,
  times_seen INTEGER NOT NULL DEFAULT 1,
  identity_confidence REAL NOT NULL DEFAULT 1,
  is_dormant INTEGER NOT NULL DEFAULT 0,
  game_version TEXT
);
CREATE INDEX IF NOT EXISTS ix_profiles_fingerprint ON npc_profiles(fingerprint);
CREATE INDEX IF NOT EXISTS ix_profiles_model ON npc_profiles(model_hash);
CREATE INDEX IF NOT EXISTS ix_profiles_seen ON npc_profiles(last_seen_utc);

CREATE TABLE IF NOT EXISTS npc_emotions (
  npc_id TEXT PRIMARY KEY,
  state_json TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS npc_memories (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  npc_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  content TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  importance REAL NOT NULL DEFAULT 0.4,
  sentiment REAL NOT NULL DEFAULT 0,
  keywords TEXT NOT NULL DEFAULT '',
  subject_npc_id TEXT,
  conversation_id TEXT,
  created_utc TEXT NOT NULL,
  last_accessed_utc TEXT NOT NULL,
  access_count INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_memories_npc ON npc_memories(npc_id, created_utc DESC);
CREATE INDEX IF NOT EXISTS ix_memories_hash ON npc_memories(npc_id, content_hash);
CREATE INDEX IF NOT EXISTS ix_memories_subject ON npc_memories(subject_npc_id);

CREATE TABLE IF NOT EXISTS npc_relationships (
  npc_id TEXT NOT NULL,
  target_id TEXT NOT NULL,
  trust REAL NOT NULL DEFAULT 0,
  affinity REAL NOT NULL DEFAULT 0,
  fear REAL NOT NULL DEFAULT 0,
  respect REAL NOT NULL DEFAULT 0,
  familiarity REAL NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'stranger',
  notes TEXT NOT NULL DEFAULT '',
  last_interaction_utc TEXT NOT NULL,
  PRIMARY KEY (npc_id, target_id)
);

CREATE TABLE IF NOT EXISTS conversation_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  conversation_id TEXT NOT NULL,
  npc_id TEXT NOT NULL,
  speaker TEXT NOT NULL,
  text TEXT NOT NULL,
  created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_history_conversation ON conversation_history(conversation_id, id);
CREATE INDEX IF NOT EXISTS ix_history_npc ON conversation_history(npc_id, id DESC);

CREATE TABLE IF NOT EXISTS world_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  event_type TEXT NOT NULL,
  x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL,
  radius REAL NOT NULL DEFAULT 20,
  magnitude REAL NOT NULL DEFAULT 1,
  description TEXT NOT NULL DEFAULT '',
  zone TEXT NOT NULL DEFAULT '',
  created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_created ON world_events(created_utc DESC);

CREATE TABLE IF NOT EXISTS neighborhood_knowledge (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  neighborhood TEXT NOT NULL,
  topic TEXT NOT NULL,
  content TEXT NOT NULL,
  source_npc_id TEXT,
  confidence REAL NOT NULL DEFAULT 0.5,
  created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_knowledge_zone ON neighborhood_knowledge(neighborhood, created_utc DESC);

CREATE TABLE IF NOT EXISTS voice_catalog (
  voice_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  gender TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  pitch TEXT NOT NULL DEFAULT 'medium',
  accent TEXT NOT NULL DEFAULT 'American',
  persona TEXT NOT NULL DEFAULT '',
  energy REAL NOT NULL DEFAULT 0.5,
  is_fallback INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS voice_assignments (
  npc_id TEXT PRIMARY KEY,
  voice_id TEXT NOT NULL,
  style TEXT NOT NULL DEFAULT '',
  assigned_utc TEXT NOT NULL,
  reason TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS audio_cache (
  cache_key TEXT PRIMARY KEY,
  path TEXT NOT NULL,
  bytes INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL,
  last_used_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS player_reputation (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  value REAL NOT NULL DEFAULT 0,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS system_settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS api_usage_daily (
  day TEXT PRIMARY KEY,
  requests INTEGER NOT NULL DEFAULT 0,
  estimated_tokens INTEGER NOT NULL DEFAULT 0
);

INSERT OR IGNORE INTO player_reputation(id, value, updated_utc) VALUES(1, 0, datetime('now'));
INSERT INTO schema_migrations(version, applied_utc) VALUES(1, datetime('now'));
").ConfigureAwait(false);
                current = 1;
                Log.Info("Applied database migration 1");
            }

            if (current < 2)
            {
                await ExecuteAsync(@"
ALTER TABLE npc_profiles ADD COLUMN last_game_zone TEXT NOT NULL DEFAULT '';
CREATE TABLE IF NOT EXISTS npc_identity_evidence (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  npc_id TEXT NOT NULL,
  evidence TEXT NOT NULL,
  confidence REAL NOT NULL,
  observed_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_evidence_npc ON npc_identity_evidence(npc_id, observed_utc DESC);
INSERT INTO schema_migrations(version, applied_utc) VALUES(2, datetime('now'));
").ConfigureAwait(false);
                Log.Info("Applied database migration 2");
            }

            if (current < 3)
            {
                // Voice assignments now record the sex they were written for and
                // the catalog version, and the mislabelled Sadachbia row is fixed
                // in place so already-seeded databases agree with the code.
                await ExecuteAsync(@"
ALTER TABLE voice_assignments ADD COLUMN is_male INTEGER NOT NULL DEFAULT 1;
ALTER TABLE voice_assignments ADD COLUMN catalog_version INTEGER NOT NULL DEFAULT 0;
UPDATE voice_catalog SET gender='male' WHERE voice_id='Sadachbia';
INSERT INTO schema_migrations(version, applied_utc) VALUES(3, datetime('now'));
").ConfigureAwait(false);
                Log.Info("Applied database migration 3");
            }
        }

        private async Task SeedVoicesAsync()
        {
            foreach (VoiceOption v in VoiceCatalog.All)
            {
                // Upsert, not insert-or-ignore: a corrected gender in the code
                // catalog must reach a database seeded by an older build.
                await ExecuteAsync(@"INSERT INTO voice_catalog
(voice_id,name,gender,description,pitch,accent,persona,energy,is_fallback)
VALUES($id,$name,$gender,$desc,$pitch,$accent,$persona,$energy,$fallback)
ON CONFLICT(voice_id) DO UPDATE SET name=excluded.name, gender=excluded.gender,
 description=excluded.description, pitch=excluded.pitch, accent=excluded.accent,
 persona=excluded.persona, energy=excluded.energy, is_fallback=excluded.is_fallback;",
                    ("$id", v.VoiceId), ("$name", v.Name), ("$gender", v.Gender), ("$desc", v.Description),
                    ("$pitch", v.Pitch), ("$accent", v.Accent), ("$persona", v.Persona),
                    ("$energy", v.Energy), ("$fallback", v.IsFallback ? 1 : 0)).ConfigureAwait(false);
            }
        }

        // ---------------- profiles ----------------

        public async Task<NpcProfile> FindByFingerprintAsync(string fingerprint)
        {
            if (!IsAvailable || string.IsNullOrEmpty(fingerprint)) return null;
            await using var reader = await QueryAsync("SELECT * FROM npc_profiles WHERE fingerprint=$f ORDER BY last_seen_utc DESC LIMIT 1;",
                ("$f", fingerprint)).ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? MapProfile(reader) : null;
        }

        public async Task<NpcProfile> GetProfileAsync(string npcId)
        {
            if (!IsAvailable || string.IsNullOrEmpty(npcId)) return null;
            await using var reader = await QueryAsync("SELECT * FROM npc_profiles WHERE npc_id=$id;", ("$id", npcId)).ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? MapProfile(reader) : null;
        }

        public async Task<List<NpcProfile>> GetCandidatesAsync(int modelHash, DateTime seenSinceUtc, int limit = 40)
        {
            var list = new List<NpcProfile>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(
                "SELECT * FROM npc_profiles WHERE model_hash=$m AND last_seen_utc >= $since ORDER BY last_seen_utc DESC LIMIT $limit;",
                ("$m", modelHash), ("$since", seenSinceUtc.ToString("o")), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapProfile(reader));
            return list;
        }

        public async Task<List<NpcProfile>> GetRecentProfilesAsync(DateTime seenSinceUtc, int limit = 100)
        {
            var list = new List<NpcProfile>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(
                "SELECT * FROM npc_profiles WHERE last_seen_utc >= $since ORDER BY last_seen_utc DESC LIMIT $limit;",
                ("$since", seenSinceUtc.ToString("o")), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapProfile(reader));
            return list;
        }

        public async Task SaveProfileAsync(NpcProfile p)
        {
            if (!IsAvailable || p == null) return;
            await ExecuteAsync(@"INSERT INTO npc_profiles
(npc_id,fingerprint,model_hash,model_name,is_male,display_name,age,occupation,category,neighborhood,background,
 personality_json,personality_desc,speech_json,interests_json,beliefs_json,quirks_json,voice_json,
 created_utc,last_seen_utc,last_x,last_y,last_z,times_seen,identity_confidence,is_dormant,game_version,last_game_zone)
VALUES($id,$fp,$mh,$mn,$male,$name,$age,$occ,$cat,$hood,$bg,$pj,$pd,$sj,$ij,$bj,$qj,$vj,$created,$seen,$x,$y,$z,$times,$conf,$dormant,$gv,$zone)
ON CONFLICT(npc_id) DO UPDATE SET
 fingerprint=excluded.fingerprint, model_hash=excluded.model_hash, model_name=excluded.model_name,
 is_male=excluded.is_male, display_name=excluded.display_name, age=excluded.age, occupation=excluded.occupation,
 category=excluded.category, neighborhood=excluded.neighborhood, background=excluded.background,
 personality_json=excluded.personality_json, personality_desc=excluded.personality_desc, speech_json=excluded.speech_json,
 interests_json=excluded.interests_json, beliefs_json=excluded.beliefs_json, quirks_json=excluded.quirks_json,
 voice_json=excluded.voice_json, last_seen_utc=excluded.last_seen_utc, last_x=excluded.last_x, last_y=excluded.last_y,
 last_z=excluded.last_z, times_seen=excluded.times_seen, identity_confidence=excluded.identity_confidence,
 is_dormant=excluded.is_dormant, game_version=excluded.game_version, last_game_zone=excluded.last_game_zone;",
                ("$id", p.NpcId), ("$fp", p.Fingerprint ?? ""), ("$mh", p.ModelHash), ("$mn", p.ModelName ?? ""),
                ("$male", p.IsMale ? 1 : 0), ("$name", p.DisplayName), ("$age", p.Age), ("$occ", p.Occupation ?? ""),
                ("$cat", p.Category ?? NpcCategories.Civilian), ("$hood", p.Neighborhood ?? ""), ("$bg", p.Background ?? ""),
                ("$pj", Json(p.Personality)), ("$pd", p.PersonalityDescription ?? ""), ("$sj", Json(p.Speech)),
                ("$ij", Json(p.Interests)), ("$bj", Json(p.Beliefs)), ("$qj", Json(p.Quirks)), ("$vj", Json(p.Voice)),
                ("$created", p.CreatedUtc.ToString("o")), ("$seen", p.LastSeenUtc.ToString("o")),
                ("$x", p.LastKnownPosition?.X ?? 0), ("$y", p.LastKnownPosition?.Y ?? 0), ("$z", p.LastKnownPosition?.Z ?? 0),
                ("$times", p.TimesSeen), ("$conf", p.IdentityConfidence), ("$dormant", p.IsDormant ? 1 : 0),
                ("$gv", p.GameVersion ?? ""), ("$zone", p.Neighborhood ?? "")).ConfigureAwait(false);
        }

        public async Task<int> CountProfilesAsync()
        {
            if (!IsAvailable) return 0;
            return Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM npc_profiles;").ConfigureAwait(false));
        }

        public async Task MarkDormantAsync(IEnumerable<string> activeIds)
        {
            if (!IsAvailable) return;
            var keep = new HashSet<string>(activeIds ?? Enumerable.Empty<string>());
            var toDormant = new List<string>();
            // The reader holds the connection gate, so finish reading and release it
            // before the updates below: awaiting ExecuteAsync inside the reader scope
            // deadlocked on that same gate and froze every later database call.
            {
                await using var reader = await QueryAsync("SELECT npc_id FROM npc_profiles WHERE is_dormant=0;").ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    string id = reader.GetString(0);
                    if (!keep.Contains(id)) toDormant.Add(id);
                }
            }
            foreach (string id in toDormant)
                await ExecuteAsync("UPDATE npc_profiles SET is_dormant=1 WHERE npc_id=$id;", ("$id", id)).ConfigureAwait(false);
        }

        private NpcProfile MapProfile(ReaderScope r)
        {
            var p = new NpcProfile
            {
                NpcId = r.GetString(r.GetOrdinal("npc_id")),
                Fingerprint = r.GetString(r.GetOrdinal("fingerprint")),
                ModelHash = r.GetInt32(r.GetOrdinal("model_hash")),
                ModelName = r.GetString(r.GetOrdinal("model_name")),
                IsMale = r.GetInt32(r.GetOrdinal("is_male")) != 0,
                DisplayName = r.GetString(r.GetOrdinal("display_name")),
                Age = r.GetInt32(r.GetOrdinal("age")),
                Occupation = r.GetString(r.GetOrdinal("occupation")),
                Category = r.GetString(r.GetOrdinal("category")),
                Neighborhood = r.GetString(r.GetOrdinal("neighborhood")),
                Background = r.GetString(r.GetOrdinal("background")),
                PersonalityDescription = r.GetString(r.GetOrdinal("personality_desc")),
                CreatedUtc = ParseDate(r.GetString(r.GetOrdinal("created_utc"))),
                LastSeenUtc = ParseDate(r.GetString(r.GetOrdinal("last_seen_utc"))),
                TimesSeen = r.GetInt32(r.GetOrdinal("times_seen")),
                IdentityConfidence = r.GetDouble(r.GetOrdinal("identity_confidence")),
                IsDormant = r.GetInt32(r.GetOrdinal("is_dormant")) != 0,
                GameVersion = r.IsDBNull(r.GetOrdinal("game_version")) ? "" : r.GetString(r.GetOrdinal("game_version")),
                LastKnownPosition = new Vec3(
                    (float)r.GetDouble(r.GetOrdinal("last_x")),
                    (float)r.GetDouble(r.GetOrdinal("last_y")),
                    (float)r.GetDouble(r.GetOrdinal("last_z")))
            };
            p.Personality = Deserialize(r.GetString(r.GetOrdinal("personality_json")), new PersonalityTraits());
            p.Speech = Deserialize(r.GetString(r.GetOrdinal("speech_json")), new SpeechStyle());
            p.Interests = Deserialize(r.GetString(r.GetOrdinal("interests_json")), new List<string>());
            p.Beliefs = Deserialize(r.GetString(r.GetOrdinal("beliefs_json")), new List<string>());
            p.Quirks = Deserialize(r.GetString(r.GetOrdinal("quirks_json")), new List<string>());
            p.Voice = Deserialize(r.IsDBNull(r.GetOrdinal("voice_json")) ? null : r.GetString(r.GetOrdinal("voice_json")), (VoiceAssignment)null);
            return p;
        }

        // ---------------- emotions ----------------

        public async Task<EmotionState> GetEmotionAsync(string npcId)
        {
            if (!IsAvailable) return new EmotionState();
            await using var reader = await QueryAsync("SELECT state_json FROM npc_emotions WHERE npc_id=$id;", ("$id", npcId)).ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return new EmotionState();
            return Deserialize(reader.GetString(0), new EmotionState());
        }

        public async Task SaveEmotionAsync(string npcId, EmotionState e)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO npc_emotions(npc_id,state_json,updated_utc) VALUES($id,$s,$u)
ON CONFLICT(npc_id) DO UPDATE SET state_json=excluded.state_json, updated_utc=excluded.updated_utc;",
                ("$id", npcId), ("$s", Json(e)), ("$u", e.UpdatedUtc.ToString("o"))).ConfigureAwait(false);
        }

        // ---------------- memories ----------------

        public async Task<long> AddMemoryAsync(MemoryItem m)
        {
            if (!IsAvailable) return 0;
            string hash = Hash(Normalize(m.Content));
            await ExecuteAsync(@"INSERT INTO npc_memories
(npc_id,kind,content,content_hash,importance,sentiment,keywords,subject_npc_id,conversation_id,created_utc,last_accessed_utc,access_count)
VALUES($npc,$kind,$content,$hash,$imp,$sent,$kw,$subj,$conv,$created,$accessed,0);",
                ("$npc", m.NpcId), ("$kind", m.Kind ?? "conversation"), ("$content", Truncate(m.Content, 900)),
                ("$hash", hash), ("$imp", m.Importance), ("$sent", m.Sentiment), ("$kw", m.Keywords ?? ""),
                ("$subj", (object)m.SubjectNpcId ?? DBNull.Value), ("$conv", (object)m.ConversationId ?? DBNull.Value),
                ("$created", m.CreatedUtc.ToString("o")), ("$accessed", m.LastAccessedUtc.ToString("o"))).ConfigureAwait(false);
            return Convert.ToInt64(await ScalarAsync("SELECT last_insert_rowid();").ConfigureAwait(false));
        }

        public async Task<MemoryItem> FindRecentDuplicateAsync(string npcId, string content, DateTime sinceUtc)
        {
            if (!IsAvailable) return null;
            string hash = Hash(Normalize(content));
            await using var reader = await QueryAsync(@"SELECT * FROM npc_memories WHERE npc_id=$npc AND content_hash=$hash AND created_utc >= $since
ORDER BY created_utc DESC LIMIT 1;", ("$npc", npcId), ("$hash", hash), ("$since", sinceUtc.ToString("o"))).ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? MapMemory(reader) : null;
        }

        public async Task<List<MemoryItem>> GetMemoriesAsync(string npcId, int limit = 250)
        {
            var list = new List<MemoryItem>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(@"SELECT * FROM npc_memories WHERE npc_id=$npc
ORDER BY importance DESC, created_utc DESC LIMIT $limit;", ("$npc", npcId), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapMemory(reader));
            return list;
        }

        public async Task<List<MemoryItem>> GetMemoriesAboutAsync(string npcId, string subjectId, int limit = 12)
        {
            var list = new List<MemoryItem>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(@"SELECT * FROM npc_memories WHERE npc_id=$npc AND subject_npc_id=$subj
ORDER BY importance DESC, created_utc DESC LIMIT $limit;", ("$npc", npcId), ("$subj", subjectId), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapMemory(reader));
            return list;
        }

        public async Task TouchMemoriesAsync(IEnumerable<long> ids)
        {
            if (!IsAvailable) return;
            foreach (long id in ids.Take(50))
            {
                await ExecuteAsync("UPDATE npc_memories SET last_accessed_utc=$u, access_count=access_count+1 WHERE id=$id;",
                    ("$u", DateTime.UtcNow.ToString("o")), ("$id", id)).ConfigureAwait(false);
            }
        }

        public async Task<int> CountMemoriesAsync(string npcId)
        {
            if (!IsAvailable) return 0;
            return Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM npc_memories WHERE npc_id=$id;", ("$id", npcId)).ConfigureAwait(false));
        }

        public async Task PruneMemoriesAsync(string npcId, int maxItems)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"DELETE FROM npc_memories WHERE npc_id=$npc AND importance < 0.25
AND created_utc < $cutoff;", ("$npc", npcId), ("$cutoff", DateTime.UtcNow.AddDays(-14).ToString("o"))).ConfigureAwait(false);

            int count = await CountMemoriesAsync(npcId).ConfigureAwait(false);
            if (count <= maxItems) return;
            await ExecuteAsync(@"DELETE FROM npc_memories WHERE id IN (
SELECT id FROM npc_memories WHERE npc_id=$npc ORDER BY importance ASC, created_utc ASC LIMIT $excess);",
                ("$npc", npcId), ("$excess", count - maxItems)).ConfigureAwait(false);
        }

        private MemoryItem MapMemory(ReaderScope r)
        {
            return new MemoryItem
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                NpcId = r.GetString(r.GetOrdinal("npc_id")),
                Kind = r.GetString(r.GetOrdinal("kind")),
                Content = r.GetString(r.GetOrdinal("content")),
                Importance = (float)r.GetDouble(r.GetOrdinal("importance")),
                Sentiment = (float)r.GetDouble(r.GetOrdinal("sentiment")),
                Keywords = r.GetString(r.GetOrdinal("keywords")),
                SubjectNpcId = r.IsDBNull(r.GetOrdinal("subject_npc_id")) ? null : r.GetString(r.GetOrdinal("subject_npc_id")),
                ConversationId = r.IsDBNull(r.GetOrdinal("conversation_id")) ? null : r.GetString(r.GetOrdinal("conversation_id")),
                CreatedUtc = ParseDate(r.GetString(r.GetOrdinal("created_utc"))),
                LastAccessedUtc = ParseDate(r.GetString(r.GetOrdinal("last_accessed_utc"))),
                AccessCount = r.GetInt32(r.GetOrdinal("access_count"))
            };
        }

        // ---------------- relationships ----------------

        public async Task<Relationship> GetRelationshipAsync(string npcId, string targetId)
        {
            if (!IsAvailable) return new Relationship { NpcId = npcId, TargetId = targetId };
            await using var reader = await QueryAsync("SELECT * FROM npc_relationships WHERE npc_id=$npc AND target_id=$target;",
                ("$npc", npcId), ("$target", targetId)).ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return new Relationship { NpcId = npcId, TargetId = targetId };
            return new Relationship
            {
                NpcId = npcId,
                TargetId = targetId,
                Trust = (float)reader.GetDouble(reader.GetOrdinal("trust")),
                Affinity = (float)reader.GetDouble(reader.GetOrdinal("affinity")),
                Fear = (float)reader.GetDouble(reader.GetOrdinal("fear")),
                Respect = (float)reader.GetDouble(reader.GetOrdinal("respect")),
                Familiarity = (float)reader.GetDouble(reader.GetOrdinal("familiarity")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Notes = reader.GetString(reader.GetOrdinal("notes")),
                LastInteractionUtc = ParseDate(reader.GetString(reader.GetOrdinal("last_interaction_utc")))
            };
        }

        public async Task SaveRelationshipAsync(Relationship r)
        {
            if (!IsAvailable) return;
            r.Clamp();
            await ExecuteAsync(@"INSERT INTO npc_relationships
(npc_id,target_id,trust,affinity,fear,respect,familiarity,status,notes,last_interaction_utc)
VALUES($npc,$target,$trust,$affinity,$fear,$respect,$familiarity,$status,$notes,$last)
ON CONFLICT(npc_id,target_id) DO UPDATE SET
 trust=excluded.trust, affinity=excluded.affinity, fear=excluded.fear, respect=excluded.respect,
 familiarity=excluded.familiarity, status=excluded.status, notes=excluded.notes, last_interaction_utc=excluded.last_interaction_utc;",
                ("$npc", r.NpcId), ("$target", r.TargetId), ("$trust", r.Trust), ("$affinity", r.Affinity),
                ("$fear", r.Fear), ("$respect", r.Respect), ("$familiarity", r.Familiarity),
                ("$status", r.Status ?? "stranger"), ("$notes", r.Notes ?? ""),
                ("$last", r.LastInteractionUtc.ToString("o"))).ConfigureAwait(false);
        }

        public async Task<double> GetPlayerReputationAsync()
        {
            if (!IsAvailable) return 0;
            object value = await ScalarAsync("SELECT value FROM player_reputation WHERE id=1;").ConfigureAwait(false);
            return value == null ? 0 : Convert.ToDouble(value);
        }

        public async Task SetPlayerReputationAsync(double value)
        {
            if (!IsAvailable) return;
            await ExecuteAsync("UPDATE player_reputation SET value=$v, updated_utc=$u WHERE id=1;",
                ("$v", Math.Clamp(value, -100, 100)), ("$u", DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
        }

        // ---------------- conversations ----------------

        public async Task AddConversationTurnAsync(ConversationTurn turn)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO conversation_history(conversation_id,npc_id,speaker,text,created_utc)
VALUES($conv,$npc,$speaker,$text,$created);",
                ("$conv", turn.ConversationId ?? ""), ("$npc", turn.NpcId ?? ""), ("$speaker", turn.Speaker ?? "npc"),
                ("$text", Truncate(turn.Text, 2000)), ("$created", turn.CreatedUtc.ToString("o"))).ConfigureAwait(false);
        }

        public async Task<List<ConversationTurn>> GetRecentTurnsAsync(string conversationId, int limit = 14)
        {
            var list = new List<ConversationTurn>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(@"SELECT * FROM conversation_history WHERE conversation_id=$conv
ORDER BY id DESC LIMIT $limit;", ("$conv", conversationId ?? ""), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new ConversationTurn
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    ConversationId = reader.GetString(reader.GetOrdinal("conversation_id")),
                    NpcId = reader.GetString(reader.GetOrdinal("npc_id")),
                    Speaker = reader.GetString(reader.GetOrdinal("speaker")),
                    Text = reader.GetString(reader.GetOrdinal("text")),
                    CreatedUtc = ParseDate(reader.GetString(reader.GetOrdinal("created_utc")))
                });
            }
            list.Reverse();
            return list;
        }

        public async Task<List<ConversationTurn>> GetTurnsForNpcAsync(string npcId, int limit = 10)
        {
            var list = new List<ConversationTurn>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync(@"SELECT * FROM conversation_history WHERE npc_id=$npc
ORDER BY id DESC LIMIT $limit;", ("$npc", npcId), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new ConversationTurn
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    ConversationId = reader.GetString(reader.GetOrdinal("conversation_id")),
                    NpcId = reader.GetString(reader.GetOrdinal("npc_id")),
                    Speaker = reader.GetString(reader.GetOrdinal("speaker")),
                    Text = reader.GetString(reader.GetOrdinal("text")),
                    CreatedUtc = ParseDate(reader.GetString(reader.GetOrdinal("created_utc")))
                });
            }
            list.Reverse();
            return list;
        }

        // ---------------- world events ----------------

        public async Task AddWorldEventAsync(WorldEventRecord e)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO world_events(event_type,x,y,z,radius,magnitude,description,zone,created_utc)
VALUES($type,$x,$y,$z,$radius,$mag,$desc,$zone,$created);",
                ("$type", e.EventType), ("$x", e.Position?.X ?? 0), ("$y", e.Position?.Y ?? 0), ("$z", e.Position?.Z ?? 0),
                ("$radius", e.Radius), ("$mag", e.Magnitude), ("$desc", e.Description ?? ""), ("$zone", e.Zone ?? ""),
                ("$created", e.CreatedUtc.ToString("o"))).ConfigureAwait(false);
        }

        public async Task<List<WorldEventRecord>> GetRecentEventsAsync(DateTime sinceUtc, int limit = 40)
        {
            var list = new List<WorldEventRecord>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync("SELECT * FROM world_events WHERE created_utc >= $since ORDER BY id DESC LIMIT $limit;",
                ("$since", sinceUtc.ToString("o")), ("$limit", limit)).ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new WorldEventRecord
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    EventType = reader.GetString(reader.GetOrdinal("event_type")),
                    Position = new Vec3((float)reader.GetDouble(reader.GetOrdinal("x")), (float)reader.GetDouble(reader.GetOrdinal("y")), (float)reader.GetDouble(reader.GetOrdinal("z"))),
                    Radius = (float)reader.GetDouble(reader.GetOrdinal("radius")),
                    Magnitude = (float)reader.GetDouble(reader.GetOrdinal("magnitude")),
                    Description = reader.GetString(reader.GetOrdinal("description")),
                    Zone = reader.GetString(reader.GetOrdinal("zone")),
                    CreatedUtc = ParseDate(reader.GetString(reader.GetOrdinal("created_utc")))
                });
            }
            return list;
        }

        // ---------------- voices ----------------

        public async Task<List<VoiceOption>> GetVoiceCatalogAsync()
        {
            var list = new List<VoiceOption>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync("SELECT * FROM voice_catalog ORDER BY name;").ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new VoiceOption
                {
                    VoiceId = reader.GetString(reader.GetOrdinal("voice_id")),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    Gender = reader.GetString(reader.GetOrdinal("gender")),
                    Description = reader.GetString(reader.GetOrdinal("description")),
                    Pitch = reader.GetString(reader.GetOrdinal("pitch")),
                    Accent = reader.GetString(reader.GetOrdinal("accent")),
                    Persona = reader.GetString(reader.GetOrdinal("persona")),
                    Energy = (float)reader.GetDouble(reader.GetOrdinal("energy")),
                    IsFallback = reader.GetInt32(reader.GetOrdinal("is_fallback")) != 0
                });
            }
            return list;
        }

        public async Task<VoiceAssignment> GetVoiceAssignmentAsync(string npcId)
        {
            if (!IsAvailable) return null;
            await using var reader = await QueryAsync(@"SELECT va.voice_id, va.style, va.assigned_utc, va.reason,
vc.name, vc.description, vc.accent, vc.is_fallback, va.is_male, va.catalog_version
FROM voice_assignments va LEFT JOIN voice_catalog vc ON vc.voice_id=va.voice_id WHERE va.npc_id=$id;",
                ("$id", npcId)).ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return null;
            return new VoiceAssignment
            {
                Provider = "gemini",
                VoiceId = reader.GetString(0),
                Style = reader.IsDBNull(1) ? "" : reader.GetString(1),
                AssignedUtc = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Reason = reader.IsDBNull(3) ? "" : reader.GetString(3),
                VoiceName = reader.IsDBNull(4) ? reader.GetString(0) : reader.GetString(4),
                Description = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Accent = reader.IsDBNull(6) ? "" : reader.GetString(6),
                IsFallback = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                IsMale = reader.IsDBNull(8) || reader.GetInt32(8) != 0,
                CatalogVersion = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
            };
        }

        public async Task SaveVoiceAssignmentAsync(string npcId, VoiceAssignment v)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO voice_assignments(npc_id,voice_id,style,assigned_utc,reason,is_male,catalog_version)
VALUES($npc,$voice,$style,$utc,$reason,$male,$version)
ON CONFLICT(npc_id) DO UPDATE SET voice_id=excluded.voice_id, style=excluded.style, assigned_utc=excluded.assigned_utc,
 reason=excluded.reason, is_male=excluded.is_male, catalog_version=excluded.catalog_version;",
                ("$npc", npcId), ("$voice", v.VoiceId), ("$style", v.Style ?? ""),
                ("$utc", v.AssignedUtc ?? DateTime.UtcNow.ToString("o")), ("$reason", v.Reason ?? ""),
                ("$male", v.IsMale ? 1 : 0), ("$version", v.CatalogVersion)).ConfigureAwait(false);
        }

        public async Task<Dictionary<string, int>> GetVoiceUsageCountsAsync()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!IsAvailable) return result;
            await using var reader = await QueryAsync("SELECT voice_id, COUNT(*) FROM voice_assignments GROUP BY voice_id;").ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }

        // ---------------- audio cache ----------------

        public async Task RecordAudioCacheAsync(string key, string path, long bytes)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO audio_cache(cache_key,path,bytes,created_utc,last_used_utc)
VALUES($k,$p,$b,$c,$u)
ON CONFLICT(cache_key) DO UPDATE SET path=excluded.path, bytes=excluded.bytes, last_used_utc=excluded.last_used_utc;",
                ("$k", key), ("$p", path), ("$b", bytes), ("$c", DateTime.UtcNow.ToString("o")), ("$u", DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
        }

        public async Task<List<(string Key, string Path, long Bytes, DateTime UsedUtc)>> GetAudioCacheEntriesAsync()
        {
            var list = new List<(string, string, long, DateTime)>();
            if (!IsAvailable) return list;
            await using var reader = await QueryAsync("SELECT cache_key,path,bytes,last_used_utc FROM audio_cache;").ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), ParseDate(reader.GetString(3))));
            return list;
        }

        public async Task DeleteAudioCacheEntryAsync(string key)
        {
            if (!IsAvailable) return;
            await ExecuteAsync("DELETE FROM audio_cache WHERE cache_key=$k;", ("$k", key)).ConfigureAwait(false);
        }

        // ---------------- settings / usage ----------------

        public async Task SaveSettingAsync(string key, string value)
        {
            if (!IsAvailable) return;
            await ExecuteAsync(@"INSERT INTO system_settings(key,value,updated_utc) VALUES($k,$v,$u)
ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_utc=excluded.updated_utc;",
                ("$k", key), ("$v", value), ("$u", DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
        }

        public async Task<string> GetSettingAsync(string key)
        {
            if (!IsAvailable) return null;
            object value = await ScalarAsync("SELECT value FROM system_settings WHERE key=$k;", ("$k", key)).ConfigureAwait(false);
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        public async Task RecordUsageAsync(int requests, long tokens)
        {
            if (!IsAvailable) return;
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            await ExecuteAsync(@"INSERT INTO api_usage_daily(day,requests,estimated_tokens) VALUES($d,$r,$t)
ON CONFLICT(day) DO UPDATE SET requests=requests+excluded.requests, estimated_tokens=estimated_tokens+excluded.estimated_tokens;",
                ("$d", day), ("$r", requests), ("$t", tokens)).ConfigureAwait(false);
        }

        public async Task<(int Requests, long Tokens)> GetTodayUsageAsync()
        {
            if (!IsAvailable) return (0, 0);
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            await using var reader = await QueryAsync("SELECT requests,estimated_tokens FROM api_usage_daily WHERE day=$d;", ("$d", day)).ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return (0, 0);
            return (reader.GetInt32(0), reader.GetInt64(1));
        }

        // ---------------- low-level ----------------

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] args)
        {
            if (_connection == null) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private async Task<ReaderScope> QueryAsync(string sql, params (string Name, object Value)[] args)
        {
            if (_connection == null) throw new InvalidOperationException("Database is not open.");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                SqliteDataReader reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                return new ReaderScope(reader, cmd, _gate);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        /// <summary>
        /// Holds the database gate for the lifetime of a reader so a second
        /// command cannot interleave on the shared connection.
        /// </summary>
        private sealed class ReaderScope : IAsyncDisposable
        {
            private readonly SqliteDataReader _reader;
            private readonly SqliteCommand _command;
            private readonly SemaphoreSlim _gate;
            private bool _disposed;

            public ReaderScope(SqliteDataReader reader, SqliteCommand command, SemaphoreSlim gate)
            {
                _reader = reader;
                _command = command;
                _gate = gate;
            }

            public Task<bool> ReadAsync() => _reader.ReadAsync();
            public bool Read() => _reader.Read();
            public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);
            public string GetString(int ordinal) => _reader.GetString(ordinal);
            public int GetInt32(int ordinal) => _reader.GetInt32(ordinal);
            public long GetInt64(int ordinal) => _reader.GetInt64(ordinal);
            public double GetDouble(int ordinal) => _reader.GetDouble(ordinal);
            public int GetOrdinal(string name) => _reader.GetOrdinal(name);

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                try { await _reader.DisposeAsync().ConfigureAwait(false); }
                catch { }
                try { await _command.DisposeAsync().ConfigureAwait(false); }
                catch { }
                _gate.Release();
            }
        }

        private async Task<object> ScalarAsync(string sql, params (string Name, object Value)[] args)
        {
            if (_connection == null) return null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private string Json<T>(T value) => JsonSerializer.Serialize(value, _json);

        private T Deserialize<T>(string json, T fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try { return JsonSerializer.Deserialize<T>(json, _json) ?? fallback; }
            catch { return fallback; }
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
                else sb.Append(' ');
            }
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Hash(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max);

        private static DateTime ParseDate(string value) =>
            DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.UtcNow;

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_connection != null)
                {
                    try { await _connection.CloseAsync().ConfigureAwait(false); } catch { }
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                }
            }
            finally { _gate.Release(); }
            _gate.Dispose();
        }
    }
}
