# Conversation animation and vehicle-crash work (2026-11-02)

Progress record for the animation, lip-sync and vehicle-stability pass. The
Gemini Live voice path and the engagement system from the previous pass are
unchanged.

## 1. Why GTA V closed

Two real causes were found, both of the same kind: GTA natives called from a
thread that is not the script thread. GTA's script VM is single-threaded; a
native call from anywhere else corrupts it and the process exits with no managed
exception, which is exactly the "game just closes" symptom.

1. **Voice events ran game code on the pipe reader.** `VoiceLink` dispatches on a
   `Task.Run` reader. Its `EventReceived`/`Closed` handlers called
   `OnVoiceEvent` → `StopVoiceConversation` → `ConversationHold.Release()`, which
   calls `TASK_VEHICLE_DRIVE_WANDER`, `CLEAR_PED_TASKS`,
   `SET_ENTITY_AS_NO_LONGER_NEEDED`. Every "session ended while I was driving"
   path went through it. `Ready` was the same (`_mic.Start()`, HUD message).
2. **Snapshot responses applied NPC actions on a thread-pool thread.**
   `BeginSnapshot` is `async void` with `ConfigureAwait(false)`; its continuation
   ran `ActionExecutor.Execute` for every returned command, i.e. `TASK_*` natives
   (`leave_vehicle`, `approach`, `walk_away`, `flee`, `stop`) off the game thread.

Contributing hazards: `TASK_VEHICLE_TEMP_ACTION` / `TASK_VEHICLE_DRIVE_WANDER`
were issued for any vehicle, including bicycles (class 13) and trains (21), which
have no scripted driving task that tolerates them; and nothing ended a
conversation when the player got into a vehicle, so a pedestrian hold could be
re-applied against a driving player.

## 2. Fix

- `GameThreadQueue` (`src/AnotherDay.Shared/GameThreadQueue.cs`) is the single
  bridge to the game. It is drained at the top of every script tick, at most 64
  actions per frame; a throwing action is recorded in `LastError` and the rest
  still run.
- `VoiceLink` `Ready`/`Event`/`Closed`, snapshot application
  (`Main.ApplySnapshot`) and every `StopVoiceConversation` call are posted to that
  queue. `StopVoiceConversation` also re-posts itself if it is ever reached
  off-thread, so it cannot call a native from a background thread even by
  mistake.
- `GameThread` plus guards in `ConversationHold` and `ConversationAnimator` turn a
  stray background call into a logged no-op.
- `ConversationHold.IsUnsupportedVehicle` skips the car brake/wander natives for
  bicycles and trains; those peds are released with `CLEAR_PED_TASKS`.
- `VehicleTransitionPolicy` + `ConversationEndReason.PlayerEnteredVehicle`: getting
  into a car or onto a bicycle during a conversation ends it in a defined order
  (engagement → hold release → animator release → microphone → audio → pipe).
  Starting a conversation from a seat is still allowed.
- Audio and network threads now only write atomic level/timestamp fields
  (`_npcLevel`, `_npcAudioTicks`, `_micVoiceTicks`); they never read or write an
  entity.

## 3. Animation

Pure logic lives in `src/AnotherDay.Shared/ConversationAnimation.cs` so it can be
tested without the game:

- `ConversationAnimationState` — mouth open/closed from audio level, phases
  (Idle, Listening, PlayerSpeaking, NpcSpeaking, Ending), gesture timing with
  per-style cooldown, nod cadence while listening, expression selection, `End()`.
- `ConversationStyle` — personality (friendliness, aggression, confidence,
  nervousness, humour) plus an explicit mood, with the vanilla gesture dictionary
  and facial clipset names derived from it.
- `AnimationSafety` — the gate matrix: face is allowed in a vehicle, body
  animation is not; nothing for dead/ragdolled peds; nothing while falling,
  swimming, jumping or fighting.
- `VehicleTransitionPolicy` — entering a vehicle ends; starting seated is allowed.

Native application lives in `src/AnotherDay.Plugin/Game/ConversationAnimator.cs`:

- **Mouth**: `SET_FACIAL_IDLE_ANIM_OVERRIDE(ped, "mic_chatter", "mp_facial")`
  while the RMS of the PCM chunk that just reached the speakers is above the
  threshold. `mp_facial` is requested up front and the override is applied only
  once `HAS_ANIM_DICT_LOADED` is true, twice in a row every 600 ms (the ped's
  own facial idle can consume the first application), and cleared the moment
  audio stops, on `interrupted`, and on release. Player mouth is the same
  mechanism driven by microphone VAD.
- **Gestures**: `TASK_PLAY_ANIM` with `48` (upper body + secondary task) so
  walking/driving tasks are untouched, from
  `gestures@m@standing@casual` / `gestures@f@standing@casual`; `DOES_ANIM_DICT_EXIST`
  and `HAS_ANIM_DICT_LOADED` are checked, the dictionary is requested
  asynchronously and gestures are skipped (never awaited) until it loads.
- **Expressions**: `SET_FACIAL_CLIPSET` with vanilla
  `facials@gen_male|gen_female@variations@...`; a missing set is a silent no-op.
- Ticked every frame from `UpdateAnimator()` so mouth movement follows audio at
  frame rate rather than the 100 ms AI cadence.

## 4. Tests

`dotnet test` — **72 passed, 0 failed**. New coverage:

- `ConversationAnimationTests` (12): mouth opens on audio, closes on silence, quiet
  audio ignored, player mouth from VAD, listening nods, gesture cooldown, gesture
  vocabulary per personality, restrained gestures, expression mapping, explicit
  mood override, ending clears everything, dictionary per sex.
- `AnimationSafetyTests` (6): no body animation in vehicles, face allowed in a
  vehicle, dead/ragdolled blocked, falling/swimming/combat/jumping blocked,
  standing ped allowed, player body blocked while driving.
- `VehicleTransitionPolicyTests` (4): entry ends, already seated does not,
  inactive unaffected, start-from-seat allowed.
- `GameThreadQueueTests` (3): ordering, exception isolation, per-tick bound.

## 5. Not verified (needs the game)

- Visual quality of jaw motion, gestures and expressions per ped model.
- That no other installed mod calls natives off-thread (outside this mod's control).
- The full vehicle checklist: entering a car mid-conversation, driving away,
  bicycle, starting from a seat, exiting and starting a new conversation.

The in-game checklist is in `docs/TESTING.md`, items 17–23.

## 6. Hold rework: stop the walk, then give it back (follow-up pass)

The animation pass made a partner animate, but the hold it ran under did not
actually stop a pedestrian's ambient behaviour. The hold issued
`TASK_TURN_PED_TO_FACE_ENTITY(ped, player, 1600)` every 1.5 s, so for part of
every cycle the turn task no longer existed and the pedestrian's own wander AI
owned the body again. The result was exactly the reported symptom: the NPC kept
walking - sometimes away - while the voice played, and looked past the player
because the body was still under street behaviour.

`ConversationHold` now holds with finite, renewed tasks:

- **Stand still, turn only when needed.** `TASK_STAND_STILL(ped, 3000)` is issued
  with the hold, and `TASK_TURN_PED_TO_FACE_ENTITY(ped, player, 2000)` only when
  the body is more than 25 degrees off target. Re-issuing the turn every tick
  restarted the rotation and the body never settled.
- **Head and eyes track the player.** `TASK_LOOK_AT_ENTITY(ped, player, 3000, 2048, 2)`
  is re-issued with the hold. Flag 2048 aims at the player's head rather than the
  entity origin; without it the eyes settle low and read as "not looking at you".
- **The walk stops on the `P` press**, in `Attach`, not on the first tick - no
  second of walking away while the socket opens.
- **Drivers keep the seat.** No enter/leave task is ever issued to a ped in a
  vehicle, and bicycles/trains are skipped as before.
- **Release restores ambient behaviour.** Someone on foot gets `CLEAR_PED_TASKS`,
  `CLEAR_PED_SECONDARY_TASK` and, when the ped was not already a mission entity,
  `TASK_WANDER_STANDARD(10 m, 10)`. Clearing tasks alone left a pedestrian
  standing where the conversation ended, which reads as a frozen NPC.

Every hold task is finite, so a missed release self-heals within a few seconds
instead of freezing the pedestrian permanently.

Not verified without the game: how the 25-degree threshold feels for peds that
are already mid-turn, and whether an ambient scenario can still steal the body
between two hold ticks.

The checklist is in `docs/TESTING.md` (H1-H8 and L1-L8).

## 7. Push-to-talk and gestures that actually play (repair pass)

Two separate defects were behind "the NPC talks but the body does nothing".

**The gesture clips did not exist.** Every gesture name was a plausible guess
(`gesture_explain`, `gesture_head_nod`, `gesture_disbelief`, …). `TASK_PLAY_ANIM`
with a clip that is not in the dictionary is a silent no-op, so the animator ran,
reported nothing, and moved nothing. The mapping now uses clips that are in the
shipped game: `gesture_hand_left`, `gesture_hand_down`, `gesture_shrug_hard`,
`gesture_no_way`, `gesture_displeased`, `gesture_damn`, `gesture_hello`,
`gesture_bye_hard`, `gesture_bring_it_on`, `gesture_point`, plus
`mp_gun_shop_tut/indicate_right` for pointing on both sexes. A dictionary that is
genuinely missing is remembered per dictionary and skipped, instead of turning
off every gesture for the session. Gestures are secondary upper-body tasks
(flags 48), so they never take the walk, the facing or a vehicle task.

**The turn was a toggle.** `P` started a continuous stream and `P` again ended
the whole conversation, so the model chose its own turn boundaries and could
speak while the player was still talking. `P` is now push-to-talk:

- Hold `P`: the microphone buffer is forwarded to Gemini; released: forwarding
  stops and `voice.turn.end` asks the service to send
  `realtimeInput.audioStreamEnd`, which finalises the player's turn.
- Model audio arriving while `P` is held is discarded rather than played, so the
  NPC can never talk over the player.
- Releasing `P` ends the turn, not the conversation: nothing is cancelled, and
  the answer plays out in full. `O` (`hangupKey`) is the explicit way to end.
- Server-side silence detection is a safety net, not the turn boundary; its
  default was raised to 900 ms so a pause for breath is not a turn.

Mouth movement, expressions and head tracking are unchanged: the mouth is driven
by the RMS of the PCM chunk that just reached the speakers, and the head follows
via the hold's look-at task. Lip movement is speech-timed, not phoneme-exact —
there is no viseme data in Gemini's audio stream, so this is the reliable
approximation, not a claim of frame-perfect synchronisation.


## 8. Reference alignment (2026-11-02)

A working LSPDFR plugin (NPCI) was decompiled read-only to compare techniques;
see `docs/REFERENCE-NPCI.md` for the evidence. Three differences explained the
"no mouth, no eye contact" reports and are now corrected here:

1. **Facial dictionary was never requested.** `SET_FACIAL_IDLE_ANIM_OVERRIDE`
   against a dictionary that is not resident is a silent no-op. `mp_facial` is
   now requested on attach and every second until `HAS_ANIM_DICT_LOADED` is true.
2. **Override applied once per tick.** The reference applies it twice in a row,
   because the ped's own facial idle can consume the first call; that is what we
   do now, every 600 ms instead of every 400 ms.
3. **Look flags aimed at the feet.** `TASK_LOOK_AT_ENTITY` now uses flag `2048`
   (head, not entity origin) and the body turn gets a 2000 ms task so it can
   complete instead of being restarted every hold tick.
