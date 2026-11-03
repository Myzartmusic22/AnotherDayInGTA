# Architecture

## Projects

| Project | Target | Purpose |
|---|---|---|
| `src/AnotherDay.Shared` | `netstandard2.0` | IPC protocol, frame codec, DTOs. No third-party dependency so both sides share it. |
| `src/AnotherDay.Service` | `net10.0` / `win-x64` | Background AI service: engines, Gemini, SQLite, pipe server. |
| `src/AnotherDay.Plugin` | `net48` | ScriptHookVDotNet 3 plugin: observation, UI, audio, action execution. |
| `tests/AnotherDay.Tests` | `net10.0` | xUnit tests for protocol, identity, persistence, emotion and full IPC pipeline. |

## Game plugin

`Main.cs` runs a 100 ms tick. It:

1. polls the pipe status and reconnects with backoff (starting the service
   through `ServiceLauncher` when needed);
2. every `SnapshotIntervalMs` sends `peds.snapshot` with the player state,
   nearby peds (model, position, health, vehicle, weapon, combat state) and
   world events from `WorldSensor`;
3. applies returned `npc.action` commands through `ActionExecutor` and queues
   `npc.speech` lines for playback;
4. draws the HUD status lines and handles keys. Spoken dialogue is audio-only:
   transcripts stay in the service, and `HudDialoguePolicy` keeps captions off
   the screen so the missing-glyph boxes cannot come back;
5. holds the active conversation partner through `ConversationHold`, driven by
   the shared `EngagementState` (attention, vehicle context, timers, end reason).
   Ambient tasks are blocked only for that ped and only while the conversation
   lasts; `Release` hands the ped back to ordinary pedestrian or traffic AI.
6. runs `ConversationAnimator` every frame: mouth movement from the RMS of the
   PCM chunk that reached the speakers, gestures and facial expressions from the
   NPC's personality and mood, all gated by the ped's vehicle/combat/ragdoll
   state. Upper-body flags only, so walking and driving tasks are untouched.

### Threads

GTA's script VM is single-threaded. Talking to it from the pipe reader, the
microphone callback or a `Task.Run` continuation corrupts it and the game closes
with no managed exception. Therefore:

- `GameThreadQueue` (`AnotherDay.Shared`) is the only bridge; `OnTick` drains it
  at most 64 actions per frame, and one failing action cannot stop the rest;
- voice `Ready`/`Event`/`Closed` callbacks, snapshot responses and every
  `StopVoiceConversation` call are marshalled onto the game thread, and
  `StopVoiceConversation` re-posts itself if it is ever reached off-thread;
- audio capture/playback and Gemini sockets never call natives: they only write
  atomic level/timestamp fields that the animator reads;
- `GameThread` + guards in `ConversationHold` and `ConversationAnimator` turn a
  stray background call into a logged no-op rather than a crash.

### Conversation ownership

Only one conversation exists at a time, and it is an object, not a set of flags.
`ConversationSession` (`AnotherDay.Shared`) holds the conversation id, the
monotonic generation, the NPC's persistent identity, its handle, the model sex,
the start time and the last-known position.

- Every `VoiceLink` callback is captured with the generation it was opened for;
  a handler whose generation is not live drops the frame instead of applying it
  to whoever the player is talking to now.
- `UpdateLiveSession` releases the conversation when the partner is gone, dead
  or beyond `range + 15 m`, and ends any state whose owner has disappeared - the
  stuck condition that used to keep an NPC bound forever.
- Pressing `P` beside a different pedestrian ends the old conversation
  (`PlayerSwitchedTarget`) and starts the new one in the same press. The service
  closes any other live session when a new one opens, so the previous partner
  cannot answer into the new conversation.

### Voice matching

`GeminiVoices` (`AnotherDay.Shared`) is the single, versioned classification of
Gemini's 30 voices (Sadachbia was previously mislabelled female). The plugin
reports the live model's sex with `VoiceStartRequest.NpcIsMale`; the service
corrects a stale profile, validates the stored assignment, and reassigns only the
voice when it does not fit. Voices that are not in the table are never used.

`PedCollector` builds fingerprints (`model|sex|modelHash|traitHash`) and hints a
category; `WorldSensor` diffs tick-over-tick state to emit gunshots, crashes,
fights, deaths, police escalation, aiming, zone and weather changes, each with a
cooldown so the same event is not re-sent.

Audio: `AudioPlayback` is a single sequential `WaveOut` queue (no overlapping
NPCs) with distance attenuation; player push-to-talk stops the current line,
which is the natural interruption path. `MicrophoneRecorder` captures 16 kHz
mono WAV for the transcription provider.

## Protocol

Length-prefixed (4-byte little-endian) UTF-8 frames, each a `MessageEnvelope`
with `schemaVersion`, `messageId`, `correlationId`, `type`, `sentUtc` and an
opaque `payloadJson`. A frame over 16 MiB is rejected; a schema mismatch is
rejected before any handler runs. Message types:

`hello`, `peds.snapshot`, `conversation.say`, `conversation.voice`,
`world.events`, `social.trigger`, `settings.get`, `settings.update`,
`status.get`, `ping`, plus results and `error`.

The pipe uses `PipeOptions.CurrentUserOnly` and requires a shared token in the
`hello` frame (constant-time compare). Unauthorised connections receive an
`error` frame and are dropped.

## Service engines

| Component | Responsibility |
|---|---|
| `NpcRegistry` | Maps handles to stable NPCs, binds or creates identities, tiers, persistence, snapshot assembly. |
| `IdentityGenerator` | Deterministic names, age, occupation, background, personality and speech style from a seeded RNG over the fingerprint. |
| `EmotionEngine` | Local state transitions for world events and utterances, decay toward personality baseline, flee/confront/willingness decisions. |
| `MemoryEngine` | Importance/recency/keyword scoring, duplicate detection, per-NPC pruning, prompt formatting. |
| `VoiceEngine` | Stable voice assignment from the catalogue with personality/style matching, usage balancing, persistence. |
| `ConversationEngine` | Builds context from identity, personality, emotion, relationship, memories and the scene, calls the provider, validates the structured answer, then commits emotion/memory/relationship changes and action proposals. |
| `GeminiProvider` / `GeminiClient` | Interactions API calls (text, TTS, transcription), quota and rate limits, retries, redaction. |
| `OfflineProvider` | Deterministic local dialogue so the game stays playable without a key or network. |
| `AudioService` | TTS generation with SHA-based cache keys, cache trimming, local fallback voice. |
| `SocialEngine` | Chooses NPC pairs by proximity/relationship/event importance, runs group conversations with turn order, caps active conversations. |
| `WorldEventEngine` | Converts observations into per-NPC knowledge by radius, produces reaction actions. |
| `ActionRegistry` | Whitelist of executable actions and their preconditions. |

Everything network- or disk-bound is `async`; a slot-limited queue bounds
concurrent AI requests and a per-day budget stops runaway spend.

## Data model (SQLite, migrations in `Database.cs`)

`schema_migrations`, `npc_profiles`, `npc_emotions`, `npc_identity_evidence`,
`npc_memories`, `npc_relationships`, `player_reputation`, `conversation_history`,
`world_events`, `neighborhood_knowledge`, `voice_catalog`, `voice_assignments`,
`audio_cache`, `api_usage_daily`, `system_settings`.

Migrations run inside a transaction and are recorded before they are applied;
`PRAGMA journal_mode=WAL`, foreign keys on, and a corrupt database is moved
aside (with a copy in `backups/`) so it can never stop the game from launching.

## Simulation tiers

| Tier | Content | Behaviour |
|---|---|---|
| 1 Active | Player-facing NPC / active conversation | Full context, AI allowed |
| 2 Nearby | Within the reaction radius | Social awareness, event reactions, occasional lines |
| 3 Background | Spawned elsewhere in the snapshot | Local rules, deferred persistence |
| 4 Dormant | Despawned | Identity, memories, relationships kept in SQLite only |

## AI output contract

The model must answer with JSON containing `dialogue`, optional `innerThought`,
`emotion` deltas, `memory` proposals, `relationship` deltas and `actions`.
Unknown fields are ignored; unknown action names are dropped; numeric deltas are
clamped; every action is re-checked against live entity state by the plugin.
No text from the model is ever executed as code or passed to an arbitrary
native call.

## Security

- API key: DPAPI (current user) or environment variable; redacted from logs.
- IPC: current-user-only pipe + random token, protocol version check.
- The plugin accepts only whitelisted actions for entities that still exist.
- Prompts contain only the NPC context needed for one line.
- Logs rotate daily and redact anything resembling a key.
