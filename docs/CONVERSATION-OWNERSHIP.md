# Conversation ownership, cleanup and voice matching (2026-11-02)

Progress record for the targeting, cleanup, voice-matching and acknowledgement
pass. Everything from the previous animation/vehicle pass (game-thread queue,
lip-sync, gestures, vehicle rules) is unchanged and still in place.

## 1. What the player saw

- A conversation started in the countryside stayed attached after driving into
  town, and a new pedestrian appeared to ignore the P press.
- A female pedestrian answered in a clearly masculine voice.

## 2. Root causes found

**Ownership was a set of booleans and a raw handle.** `_liveActive`,
`_liveNpcHandle` and a single `_engagement` had no generation: any frame from the
voice pipe was applied to "the current conversation", whichever NPC that had
become. Two concrete ways that went wrong:

1. `HandleVoiceReady` accepted a ready frame without checking which NPC the
   session had been opened for, so a slow handshake could bind the *previous*
   pedestrian's session into the new conversation.
2. Nothing ended the conversation at the point the new one started. Pressing P
   while a conversation was still nominally active only *stopped* it, so the new
   pedestrian got no session at all until the player pressed P a second time -
   which reads exactly as "they ignored me".

Also, `UpdateLiveSession` trusted `_engagement` alone. After a conversation was
torn down (or never fully established) `_liveActive` could remain true with no
owner, and nothing would end it - the stuck state behind a microphone that
belonged to a pedestrian hundreds of metres away.

**One wrong voice label.** The catalog classified **Sadachbia** as female;
Gemini's published voice table lists it as male. The plugin log from the reported
session shows a female NPC (Olivia Kim) assigned exactly `Sadachbia`. The
assignment code trusted the stored assignment, and `INSERT OR IGNORE` meant a
corrected catalog never reached an already-seeded database, so the wrong voice
was permanent.

## 3. Changes

Ownership (`src/AnotherDay.Shared/ConversationSession.cs`):

- unique conversation id, generation, persistent NPC id, handle, model sex,
  start time, last-known position, end reason;
- `Owns(generation)` is the single gate every voice callback goes through;
- `ShouldRelease(available, distance, radius)` is the release rule.

Plugin (`src/AnotherDay.Plugin/Main.cs`):

- `_conversation` + `_conversationGeneration` replace implicit ownership;
- every `VoiceLink` callback (ready, audio, events, closed, mic barge-in) is
  generation-tagged and dropped when it belongs to an older conversation;
- a ready frame whose `NpcId` differs from the conversation's identity is
  refused and the session is closed;
- pressing P beside a different pedestrian ends the old conversation
  (`PlayerSwitchedTarget`) and starts the new one immediately;
- an active conversation with no owner is closed as `SessionLost`;
- the partner is released when unavailable, dead, out of range, or when the
  player enters a vehicle;
- teardown order is unchanged and still game-thread only: engagement, hold,
  animator (mouth/expression/gestures), microphone, playback, link, token.

Voice matching:

- `src/AnotherDay.Shared/GeminiVoices.cs`: the verified 30-voice classification,
  versioned (`CatalogVersion = 2`), with `IsCompatible` / `DefaultFor` / `Resolve`;
  unknown ids are never used;
- `VoiceCatalog` corrected (Sadachbia male) and now exposes the same version;
- `Database` migration 3 adds `is_male` and `catalog_version` to assignments and
  fixes the seeded Sadachbia row; seed is now an upsert so future corrections
  reach existing databases;
- `VoiceEngine` validates on every use, reassigns only the voice when it does not
  fit, and records the sex + version. `LiveSessionManager` refuses a session with
  an incompatible voice;
- `PedGender` reports the live model's sex (`IS_PED_MALE`, then SHVDN model flag);
  the plugin sends it in `VoiceStartRequest.NpcIsMale`; the service corrects a
  stale profile before opening the session;
- `VoiceReadyResponse` echoes `ConversationId`, and the HUD line now shows the
  voice and the character's sex so the match is visible in game.

Acknowledgement: the existing `ConversationHold` already turns the NPC to face
the player every 1.5 s (`TASK_TURN_PED_TO_FACE_ENTITY`) and holds drivers at the
wheel; the animator adds listening nods, speaking gestures and facial
expressions. No new task was added that could fight walking or driving.

## 4. Tests

`dotnet test`: **93 passed, 0 failed** (was 72).

- `ConversationOwnershipTests` (12): unique ids, generation gating, late-frame
  rejection, driving away, despawn/death, switching targets, identity vs handle,
  last-known-position lifetime, end-reason stability.
- `VoiceGenderTests` (9): full 30-voice table, Sadachbia male, catalog agreement,
  no female voice for a male body and vice versa, unknown ids rejected, fallback
  correctness, correction of a wrong stored assignment, voice stability across
  sessions.

## 5. Not verified (needs the game)

- That `IS_PED_MALE` returns the correct sex for every ped model in the player's
  install (add-on models included); the fallback is logged if it fails.
- Audible gender match in game (the automated tests verify the rules, not the
  sound).
- Visual acknowledgement and lip-sync quality on actual ped models.
- That no other mod interferes with the pedestrian tasks.

## 6. Remaining work

- In-game run of the scenarios in `docs/TESTING.md` items 24-27 (ownership
  hand-over, female voice, male voice, acknowledgement).
- If a specific ped model is misclassified, capture the model hash from the log
  and extend the classification; no code path guesses from names.

## 2026-11-02 — One owner, one teardown

The reported defect was not a distance threshold. `UpdateLiveSession` began with
`if (!_liveActive && !_liveStarting)` and then, after the watchdog gate, with
`if (_engagement == null) return;`. In other words the only code that could
release a conversation sat behind state that a half-finished handshake never
satisfied. A connection that never produced a `voice.ready` left
`_liveStarting = true` forever; every later `P` press then took the
"already talking" branch, flushed the old NPC's audio and never selected the new
pedestrian. Travel, respawn and distance checks all ran — behind that guard.

The correction is structural:

- `ConversationOwner` (`src/AnotherDay.Shared/ConversationLifecycle.cs`) is the
  single authoritative record: phase (`Idle/Connecting/Active/Ending`),
  generation, conversation id and NPC. Microphone forwarding, Gemini audio,
  animation and the HUD ask it who the partner is instead of keeping their own
  copy of the answer.
- `Begin` refuses while any conversation owns the slot, so a new partner can
  never inherit an old session; `StartVoiceConversation` tears the old one down
  first and reports "Finishing the last conversation..." if it cannot.
- `BeginEnding` invalidates the generation **on the calling thread**, before any
  marshalling, so late pipe frames fail `Owns` immediately.
- `StopVoiceConversation`/`FinishTeardown` is the one teardown, idempotent, and
  safe for a partner that has already despawned.
- `UpdateConversationLifecycle` runs every tick *before* engagement logic and
  releases on: explicit hang-up, unanswered handshake (20 s), dead/dying player,
  a replaced player ped (respawn), despawned/dead partner, distance beyond
  `range + 15 m`, and a completed spoken goodbye.
- `P` is no longer swallowed: an expired connection is torn down on the press,
  and a farewell in progress says so.

### Spoken goodbye

`FarewellDetector` scores either speaker's **completed turn**: departure cues
are decisive (2), gratitude adds one, and a question or "but/wait/one more"
cancels two. That deliberately keeps "thank you" or "thanks, I appreciate it"
mid-conversation as an ordinary turn while "Thanks, take care", "I gotta go"
and "See you later" end it. The service raises one `farewell` event per closing
exchange; the plugin waits for the model turn and closing audio to finish, then
uses the same teardown as pressing `O` so the NPC resumes ordinary pedestrian
behaviour. Pressing `P` to interrupt cancels a pending farewell.

### Facing

`ConversationHold` now issues a finite `TASK_STAND_STILL` plus a head/eye
`TASK_LOOK_AT_ENTITY` every hold tick, and a body turn only when the ped is more
than 25 degrees off. Re-issuing `TASK_TURN_PED_TO_FACE_ENTITY` unconditionally
made the body snap around and never settle, which is why facing looked broken
even when the native ran. Peds in vehicles are still untouched.
