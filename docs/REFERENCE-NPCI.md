# Reference: NPCI (installed LSPDFR plugin) — read-only analysis

Analysed 2026-11-02 from the local GTA V installation. Nothing in the reference
was modified, moved or copied into this project.

| File | Notes |
|------|-------|
| `plugins/LSPDFR/NPCI.dll` | 1,120,768 bytes, assembly version 3.1.2.0, .NET Framework 4.8.1, name/strings obfuscated |
| `plugins/LSPDFR/BACKED UP AI FOLDER/NPCI.dll` | older backup copy |
| `plugins/LSPDFR/NPCI.ini` | settings, keybinds, `[GeminiAI]` section (API key lives here; not reproduced) |
| `plugins/LSPDFR/NPCI/NPCI_GeminiPrompt.txt` | civilian prompt |
| `plugins/LSPDFR/NPCI/NPCI_EmergencyPrompt.txt` | officer/EMS prompt |
| `plugins/LSPDFR/NPCI/NPCI_VoiceStyles.ini` | archetype voice descriptions |
| `plugins/LSPDFR/NPCI/NPCI_VoiceProfiles.ini` | per-model voice overrides |
| `plugins/LSPDFR/NPCI/NPCI_TranscriptionPhrases.txt` | phrase lists (`[DISMISS]`, `[CALL_FRIEND]`) |
| `plugins/LSPDFR/NPCI/IdleAnimations.xml`, `GenericPedAnimations.xml` | idle/gesture clip lists |
| `plugins/LSPDFR/NPCI/Audio/*.wav` | handcuff/taser sound effects |

## Framework and loading

- RagePluginHook plugin: `[assembly: Plugin("NPCI", Author = "doublehook", ...)]`,
  `GameFiber`, `Game.LocalPlayer`, `Game.LogTrivial`.
- References LSPDFR (`LSPD_First_Response.Mod.API`, `Engine.Scripting.Entities`)
  and PolicingRedefined (`PolicingRedefined.API.PedAPI`, `EventsAPI`) for
  on-duty state, personas, frisks and citations.
- ScriptHookVDotNet natives via `NativeFunction.CallByName`, strings stored
  XOR-obfuscated (`byte[i] ^ i ^ 0xAA`) behind 3,031 accessor methods; the
  table below was recovered by decoding that array statically.
- Gemini Live over WebSocket
  `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent`,
  model `models/gemini-3.1-flash-live-preview`; separate text model
  `gemini-3.5-flash-lite:generateContent`. Key read from `NPCI.ini`
  `[GeminiAI] ApiKey`. Audio via NAudio: 16 kHz mono capture,
  24 kHz mono playback, `BufferedWaveProvider` + `WaveOutEvent`,
  `WdlResamplingSampleProvider` when the device rate differs.

## Officer vs. civilian

Separate keybinds and separate prompts, not a runtime branch in one prompt:
`[VoiceRecognition] Keybind=P` for civilians, `[EmergencyPTT] TalkKey=O` for
police/EMS/fire, with `NPCI_GeminiPrompt.txt` vs `NPCI_EmergencyPrompt.txt`.
On-duty/persona data comes from LSPDFR/PR APIs. Prompt composition adds an
"NPC CONTEXT" block (name, DOB, address) plus an archetype voice description
from `NPCI_VoiceStyles.ini` (wealthy, urban, hipster, …) and optional
per-model overrides from `NPCI_VoiceProfiles.ini`.

## Physical behaviour (recovered natives)

| Technique | Reference implementation |
|-----------|--------------------------|
| Mouth | `PLAY_FACIAL_ANIM(ped, "mic_chatter", "mp_facial")` while the speaker is active, stopped with `STOP_ANIM_TASK` using a positive exit speed when speech ends |
| Emphatic facial | `PLAY_FACIAL_ANIM` with `timetable@tracy@ig_5@base` / `base_face`, and male/female `facials@gen_male@base` / `facials@gen_female@base` with `mood_normal_1`; `GET_ANIM_DURATION` drives slices of `max(0.5, duration*0.8)` s, then `STOP_ANIM_TASK(..., -4f)`, `SET_PED_RESET_FLAG(ped, 322, true)`, `CLEAR_PED_SECONDARY_TASK` |
| Body turn | `TASK_TURN_PED_TO_FACE_ENTITY(ped, player, 2000)` after `TASK_STAND_STILL(ped, ms)`; heading corrections via `GET_HEADING_FROM_VECTOR_2D`, `TASK_ACHIEVE_HEADING`, `SET_PED_DESIRED_HEADING` |
| Eye contact | `TASK_LOOK_AT_ENTITY(ped, player, -1, 2048, 2)` re-issued every 500–1000 ms in a fiber loop while the conversation is live |
| Gestures | `TASK_PLAY_ANIM(ped, dict, clip, 4f, -8f, -1, 1, 0f, false, false, false)` from `gestures@m@standing@casual` (`gesture_me`, `gesture_hello`, `gesture_you_soft`, `gesture_bye_soft`, …), `gestures@m@standing@fat`, `gestures@m@car@truck@casual@ds`; skipped when the ped is on a vehicle that does not support gestures; `SET_PED_CAN_PLAY_GESTURE_ANIMS` toggled |
| Task hygiene | Script tasks (`SCRIPT_TASK_*`) for movement/vehicles; clears secondary task and stops anim tasks after use; does not pin mission entities |

## What we adopted (and why)

1. **Gate the speaking clip on live audio.** `PLAY_FACIAL_ANIM` starts the
   `mp_facial` / `mic_chatter` clip only while a speaker is active, and
   `STOP_ANIM_TASK` stops it with a positive exit speed. The idle override was
   tested in-game but produced no visible mouth movement, so it is not used.
2. **Look task flag 2048.** Our `TASK_LOOK_AT_ENTITY(..., 0, 2)` aims at the
   entity origin; the reference uses 2048 so the head tracks the player's head.
3. **Two-second turn task.** Re-issuing a turn every hold tick restarts the
   rotation and the body never settles; the reference gives the turn 2000 ms to
   complete.
4. **Non-blocking dictionary request.** The reference blocks a fiber for up to
   1200 ms waiting for `mp_facial`; we keep the same request/loaded gate but
   never block the game thread.

## What we deliberately did not copy

- The 1200 ms blocking wait (blocks the game thread in our architecture).
- `SET_PED_RESET_FLAG(322)` and mission/task pinning: unnecessary for our
  conversation and risky for ambient pedestrian behaviour.
- Prompts, persona text and voice profile files: proprietary content, not
  reproduced. Our prompts stay in `src/AnotherDay.Service/Ai`.
- `PLAY_FACIAL_ANIM` emphatic facial clips: they are a timed "blocking" anim
  used for cuffed/pull-over scenes rather than speech; unverified in our
  context, so not adopted.

## Limits of this analysis

- The assembly is obfuscated; method names are meaningless, so behaviour is
  inferred from decoded strings, call sites and argument shapes. Numeric
  constants are exact; the intent behind them is interpretation.
- `mp_facial` residency and the visual result of the double override were not
  observed in-game by the assistant. Verification is the L8 test in
  `docs/TESTING.md`.
