# Reference: NPCI 3.1.2 reverse-engineered, and what this project reimplemented

Source folder (read-only): `F:\NEW INSTALL GAME METHOD RESOURCES`,
subfolder `6. NPCAI-3.1.2-ForPolicingRedefined` and the two shipped zips
(`17. NPCAI-3.1.2-ForPolicingRedefined.zip`,
`(NEW USE THIS) NPCAI-3.1.2-ForStopThePed.zip`). Nothing in the reference was
modified, moved or executed.

This supersedes the earlier `docs/REFERENCE-NPCI.md` notes by adding the
dependency question, the profile files and the mapping to this project's
standalone implementation.

## 1. What the reference is

| Item | Finding |
|---|---|
| `plugins/LSPDFR/NPCI.dll` | 1,120,768 bytes, assembly 3.1.2.0, .NET Framework 4.8.1, RagePluginHook plugin (`[assembly: Plugin(...)]`), string obfuscated |
| `NPCAI_API.dll`, `NPCAI_MDT_API.dll` | 6,656 bytes each; both reference only RagePluginHook and expose their own type — they are *published* APIs for other mods (callout/MDT data feed), not dependencies of the conversation system |
| `NAudio*.dll` (5 files) | WASAPI capture + `WaveOut` playback + resampling; genuinely used for microphone and speech |
| `Newtonsoft.Json.dll` | settings/serialisation |
| `WindowsInput.dll` | global key state for the keybinds |
| `RAGENativeUI.dll` | shipped, **but `NPCI.dll` contains no reference to it** (see §4) |
| `plugins/LSPDFR/NPCI.ini` | 110 lines: Gemini key, keybinds, volume, ambient speech, walkstyles |
| `NPCI_GeminiPrompt.txt` (9.5 KB) | civilian persona prompt |
| `NPCI_EmergencyPrompt.txt` (8.5 KB) | officer/EMS/fire variant |
| `NPCI_VoiceStyles.ini` (178 lines) | `[archetype] Voice=` prose descriptions (`wealthy`, `hollywood`, `urban`, `hipster`, `surfer`, `yoga`, …) prepended to the prompt |
| `NPCI_TranscriptionPhrases.txt` | `[DISMISS]`, `[CALL_FRIEND]` phrase lists |
| `IdleAnimations.xml` | one `AnimationSet` of `Dict`/`Clip` idle pairs |
| `GenericPedAnimations.xml` | 427 lines, `<Category name="Generic">` then `dict clip` strings (gestures and idles) |
| `Audio/*.wav` | cuff/uncuff/taser effects, police actions only |

Runtime behaviour (from the earlier static analysis plus these files):

- Gemini **Live** over WebSocket
  `wss://generativelanguage.googleapis.com/ws/…BidiGenerateContent`,
  model `models/gemini-3.1-flash-live-preview`, with a separate text model
  `gemini-3.5-flash-lite` for vision context (3.1.2 changelog).
- Microphone: 16 kHz mono NAudio capture; playback: 24 kHz mono `WaveOut`,
  resampled when the device rate differs.
- Physical layer: `TASK_STAND_STILL`, `TASK_TURN_PED_TO_FACE_ENTITY(…,2000)`,
  `TASK_LOOK_AT_ENTITY(…,-1,2048,2)` re-issued every 500–1000 ms,
  `SET_FACIAL_IDLE_ANIM_OVERRIDE(ped,"mic_chatter","mp_facial")` applied twice
  after `REQUEST_ANIM_DICT("mp_facial")`, gestures from
  `gestures@m@standing@casual` via `TASK_PLAY_ANIM`, and cleanup with
  `CLEAR_PED_SECONDARY_TASK` / `STOP_ANIM_TASK`.

## 2. What depends on a police framework

Recovered assembly references in `NPCI.dll`:
`RagePluginHook`, `LSPD_First_Response`, `PolicingRedefined`, `NAudio`,
`Newtonsoft.Json`, `WindowsInput`, `System.Windows.Forms`, `NPCAI_API`,
`NPCAI_MDT_API`.

LSPDFR / Policing Redefined / StopThePed are used for the police half of the
mod, not the conversation transport:

- on-duty state and the officer/partner persona (`Game.LocalPlayer`,
  `PedAPI` persona, `EventsAPI` on-duty events);
- `PedAPI` marking before a frisk, frisks, citations, licence handling — the
  whole `perform_action` catalogue in `NPCAI 3.1 - PR - Action Commands.txt`;
- the separate `[EmergencyPTT]` keybind and emergency prompt, which only exist
  because the player can be an officer.

The conversation pipeline itself — pedestrian selection near the player, Gemini
Live socket, microphone, playback, mouth/turn/look tasks — uses ScriptHookV
natives and NAudio only. Nothing in it requires a framework; the framework is
what tells the mod *who the player is*, which a civilian conversation does not
need.

## 3. XML / INI profile system

- `NPCI_VoiceStyles.ini` maps a **ped archetype** to a prose voice description
  that is injected into the system prompt; a blank `Voice=` falls back to the
  default Gemini voice.
- `IdleAnimations.xml` and `GenericPedAnimations.xml` supply the clip lists the
  mod picks from for idles and gestures, i.e. animation data is content, not
  code.
- `NPCI.ini` carries everything else: keybinds (`[VoiceRecognition] Keybind=N`,
  `[EmergencyPTT] TalkKey=Oemcomma`), volume, ambient speech interval, prompt
  reload key, taxi/dismiss/targeting keys, walkstyle list.
- Prompt files hold persona/action rules as plain text, reloadable without
  restarting the plugin.

## 4. Is `RAGENativeUI.dll` required?

**No.** A byte scan of `NPCI.dll` finds `RagePluginHook`,
`LSPD_First_Response`, `PolicingRedefined`, `NAudio`, `Newtonsoft`,
`WindowsInput`, `System.Windows.Forms`, `NPCAI_API` and `NPCAI_MDT_API`, but no
`RAGENativeUI` reference at all. `RAGENativeUI.dll` is shipped alongside because
the package also targets other LSPDFR plugins in the same folder (and
`LICENSE - RAGENativeUI.md` is bundled for attribution); the conversation code
does not call it. It is a menu/notification library over ScriptHookVDotNet, so
even where it is used it is interface-only.

This project therefore does not reference it: the indicator, status and
settings use ScriptHookVDotNet's own `TextElement`/`Screen` drawing.

## 5. What this project implements instead

The standalone replacement is three projects, no RagePluginHook, no LSPDFR, no
Policing Redefined, no StopThePed and no other NPC mod:

| Concern | Reference | Here |
|---|---|---|
| Host | RagePluginHook plugin in `plugins/LSPDFR` | ScriptHookVDotNet 3 plugin (`net48`) in the game root, loaded by the ASI loader |
| World/AI work | RPH `GameFiber` | `Main` script tick + `GameThreadQueue`; background threads never call natives |
| Key input | `WindowsInput` global key state | ScriptHookVDotNet `KeyDown`/`KeyUp`, marshalled onto the game thread |
| Gemini key | plain text in `NPCI.ini` | DPAPI-protected secret in `%LOCALAPPDATA%\AnotherDayInGTA`, never logged |
| Gemini Live | socket from the plugin process | socket in `AnotherDay.Service.exe` (`GeminiLiveClient`), plugin talks to it over a local named pipe |
| Capture/playback | NAudio in-process | NAudio-compatible 16 kHz capture (`MicrophoneRecorder`/`MicStreamer`), 24 kHz `WaveOut` playback (`PcmPlayer`) |
| Personas | `NPCI_VoiceStyles.ini` + prompt text | `IdentityGenerator` + `VoiceEngine` + `GeminiVoices` (30-voice table with gender compatibility), persisted per NPC in SQLite |
| Animation content | `IdleAnimations.xml`, `GenericPedAnimations.xml` | `ConversationAnimation.cs` gesture/expression model + `ConversationAnimator` |
| Duty integration | LSPDFR/PR APIs | none — any pedestrian, on foot or in a car, no duty state |

## 6. Interaction contract (this build)

One key (`P` by default):

1. **First press** selects the nearest pedestrian in range, opens the session,
   makes them stop and face the player, and opens the microphone as soon as the
   session is ready. No second press is needed to start talking.
2. **While active**, hold `P` to speak and release to submit; the NPC's answer
   plays to the end even after release.
3. A tap with no speech does not submit an empty turn.
4. `O` ends the conversation. Walking further than range + 15 m, dying,
   respawning or the pedestrian despawning also ends it.

The state machine is `AnotherDay.Shared/TalkStateMachine.cs` and is unit-tested
without a game process (`tests/AnotherDay.Tests/TalkStateMachineTests.cs`).

## 7. Install (standalone)

```
1. ScriptHookV + ScriptHookVDotNet 3 (ASI loader) in the GTA V folder
2. run tools\install.ps1   (copies the plugin, the shared assembly, the service
   and the tools into the game folder and %LOCALAPPDATA%\AnotherDayInGTA)
3. AnotherDay.Service.exe --set-key   (stores the Gemini key with DPAPI)
4. launch Story Mode, walk up to any pedestrian, press P
```

## 8. Limits of the analysis

- `NPCI.dll` is obfuscated: method names are meaningless and the findings come
  from decoded strings, metadata references and call shapes. Numbers are exact;
  intent is interpretation.
- The reference was never executed by the assistant; no in-game comparison
  between it and this plugin was performed.
- The reference's prompt text and voice-style prose are the author's content and
  were not copied; this project writes its own prompts.
