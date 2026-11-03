# Restoration record: back to the animation/vehicle pass (2026-11-02 18:09 build)

This document replaces the interaction-targeting design that was written for the
pass now reverted. It records what was restored, to which state, and why.

## The restoration point

The project has no Git repository, so there is no commit to check out. The only
real history is (a) `docs/*.md`, which are written per pass, and (b) the
timestamped copies `tools\install.ps1` leaves beside every installed binary in
`<game>\scripts`.

Decompiling the checkpoint binaries with `ilspycmd` identified the exact stage:

| Checkpoint binary | Installed | Contents | Verdict |
|---|---|---|---|
| `AnotherDay.Plugin.dll.bak-20261102-180912` | 18:09:12 | `ConversationHold`, `ConversationAnimator`, `CurrentRange()` + `NearestBinding(range)` targeting, no `ConversationTargetSelector`, no `_owner`, no `Heading`/`HeadingTowards` | **restoration point** (animation/vehicle pass) |
| `…bak-20261102-185317` | 18:53:17 | adds `_owner` / `UpdateConversationLifecycle` / `FinishTeardown` (ownership pass) | later |
| `…bak-20261102-193939` | 19:39:39 | adds `ConversationTargetSelector`, `InteractionRange`, `ForwardHalfAngle` (targeting pass) | later, reverted here |

`docs/CONVERSATION-OWNERSHIP.md` independently pins the boundary: it is the
"targeting, cleanup, voice-matching and acknowledgement pass" and states that
"everything from the previous animation/vehicle pass (game-thread queue,
lip-sync, gestures, vehicle rules) is unchanged and still in place". That
previous pass is exactly the 18:09 binary.

## What was reverted

1. **Pedestrian targeting.** Deleted `AnotherDay.Plugin/Game/ConversationTargetSelector.cs`,
   `AnotherDay.Shared/ConversationTargeting.cs` and
   `tests/AnotherDay.Tests/ConversationTargetingTests.cs`. `Main.StartVoiceConversation`
   picks the partner the way the 18:09 build did: an explicit preferred handle
   if it is alive and within range, otherwise `NearestBinding(range)` - the
   nearest pedestrian the service has bound. The message is the original
   `No one close enough to talk to.`
2. **Interaction range.** `InteractionRange()` (the five-foot bubble) is gone;
   `CurrentRange()` is back, returning the configured `ConversationRangeMeters`
   (default 30, clamp 5-80). The `InteractionRangeMeters` and
   `InteractionForwardHalfAngleDegrees` config keys were removed with it.
   There is no forward cone, line-of-sight test, or closeness ranking.
3. **Facing and stopping.** `ConversationHold` is restored to the 18:09
   implementation: on each hold tick it issues `TASK_LOOK_AT_ENTITY(ped,
   player, 3000, 0, 2)` and `TASK_TURN_PED_TO_FACE_ENTITY(ped, player, 3000)`
   and nothing else. Removed: `TASK_STAND_STILL`, the 25-degree heading gate and
   its `Heading`/`HeadingTowards`/`Normalize` helpers, the 2000 ms turn task, and
   the 2048 look flag.
4. **Descriptions.** The indicator label falls back to `someone`, as before,
   instead of describing the model.

## What was deliberately kept

- Gemini Live voice, `VoiceLink`, push-to-talk press/release, hang-up, the
  ownership/lifecycle machinery (`ConversationOwner`, `ConversationLifecycle`,
  `FinishTeardown`) and the live-voice service side. These are the working
  conversation infrastructure and were not part of the reverted pass.
- `ConversationAnimator` (lip-sync, gestures, expressions) is untouched: it
  only reads audio levels and cannot gate a conversation from starting.
- Key handling stays marshalled onto the game thread
  (`_gameThread.Post(...)` in `OnKeyDown`/`OnKeyUp`). This is not a new feature;
  without it the world reads in `StartVoiceConversation` ran on the keyboard
  hook's thread.

## Vehicle behaviour

The vehicle rules are unchanged by this restoration and are the ones the
animation/vehicle pass introduced:

- `ConversationHold.Apply` returns immediately for `EngagementHold.StayInVehicle`
  and for anybody in a vehicle, so no driving, braking or look task is ever put
  on a driver or passenger.
- `Release` only clears tasks and adds `TASK_WANDER_STANDARD` for a pedestrian
  who is on foot and was not already a mission entity. A ped in a car keeps its
  seat and its driving task, and therefore keeps the ordinary ability to get out
  when the game decides to.

## Checks run for this restoration

- `dotnet build` for the plugin and the service.
- `dotnet test` for the existing suite.
- `tools\install.ps1`, then MD5 of the built and installed plugin/shared DLLs.
- `tools\smoke-test.ps1` against the installed service (handshake, snapshot and
  one conversation) so the Gemini/voice path is exercised, not just compiled.

In-game behaviour (a pedestrian actually answering) cannot be verified here.
