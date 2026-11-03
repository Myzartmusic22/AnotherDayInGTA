# Compatibility report

All values below were read from this machine during development, not assumed
from documentation.

## Verified environment

| Item | Observed value | How it was read |
|---|---|---|
| Game edition | GTA V **Legacy** (not Enhanced) | `GTA5.exe` file version |
| Game build | **1.0.3889.0** | `GTA5.exe` FileVersion |
| ScriptHookV | **3889.0.1158.13** | `ScriptHookV.dll` FileVersion |
| ScriptHookVDotNet loader | `ScriptHookVDotNet-Loader.asi` present in game root | folder listing |
| SHVDN API assembly | **3.7.0.201** (installed by this project) | `ScriptHookVDotNet3.dll` FileVersion |
| Previous SHVDN assembly | **3.7.0.167**, backed up | `AnotherDayInGTA_backup_shvdn\ScriptHookVDotNet3.dll` |
| .NET SDK | **10.0.401** | `dotnet --version` |
| .NET runtimes | Microsoft.NETCore.App **10.0.12**, WindowsDesktop 10.0.12 | `dotnet --list-runtimes` |
| .NET Framework | 4.8 present (plugin target) | build succeeded against `net48` |
| Data directory | `%LOCALAPPDATA%\AnotherDayInGTA` | service `--print-data-dir` |

## Compatibility decisions

1. **Framework: SHVDN 3 (nightly 3.7.0.201).** The pre-installed 3.7.0.167
   assembly had no matching loader, so ScriptHookVDotNet never started. The
   3.7.0.201 nightly ships a loader that supports game build 3889 and exposes
   the 3.x API this plugin compiles against. The old DLL was copied to
   `<game>\AnotherDayInGTA_backup_shvdn\` before replacement, so the change is
   reversible.
2. **RAGE Plugin Hook was rejected.** It is built for LSPDFR-style policing and
   does not give a lower-level ambient-ped loop than SHVDN does here; using both
   would add a second injection framework and more crash surface.
3. **Plugin targets `net48`.** That is the runtime ScriptHookVDotNet 3 hosts.
   The service targets `net10.0` (`win-x64`) so it can use modern SQLite and
   cryptography APIs and run independently of the game process.
4. **IPC is a named pipe with `PipeOptions.CurrentUserOnly`** plus a 32-byte
   random token written to the data directory on first run. Another local
   process cannot drive the service by guessing the pipe name.
5. **No GTA installation files were modified.** The only additions are plugin
   DLLs in `<game>\scripts` and the backed-up SHVDN assembly.
6. **No operating-system speech is used anywhere.** `System.Speech` and its
   package were removed from the service. NPC audio is Gemini Live native audio
   (player conversations) or Gemini TTS (autonomous chatter). With no working
   key a line is dropped rather than read by a Windows voice, and no dialogue is
   captioned: the HUD only reports whether an NPC is speaking or listening. A key Google rejects
   (HTTP 401/403) counts as offline, so the HUD never claims the AI is online
   while every request is refused; `--check-key` verifies a stored key and
   `--live-test` verifies the Live WebSocket path.
7. **Live allows one voice per session**, so each speaking NPC holds its own
   session (default limit 2). Sessions are proximity-based: only the NPC the
   player addresses, or a nearby pair in an audible scene, keep a socket open;
   everyone else stays on the snapshot simulation.

## Existing mods observed (coexistence notes)

The game folder already contains ScriptHookV plugins: ELS, `iFruitAddon2`,
`ManualTransmission`, `TrainerV`, `SimpleHUD`, `EnableAllInteriors`, `Persistence`,
`DamageTrackerLib`, `CalloutInterfaceAPI`, `SpeedLimitShower`, `RadarZoom`,
`Gears`, `Chauffeur`-style script DLLs and others.

- **No shared file names.** This project ships `AnotherDay.*`, `Newtonsoft.Json`
  and `NAudio*`. `Newtonsoft.Json.dll` may already exist in `scripts`; the
  installer backs up the existing copy before replacing it, and the version used
  here is the standard 13.x assembly that other script mods also load.
- **Input keys.** Defaults are `Alt+Shift+8` (chat), `P` (talk),
  `Alt+Shift+7` (social) and `Alt+Shift+6` (panel). The plain function keys are
  deliberately avoided: Story Mode uses F8/F10 for character switching, and
  RagePluginHook opens its console on F4. Push-to-talk uses a plain `P` so the
  `Shift` chord does not collide with sprinting/weapon selection; `P` is unbound
  in Story Mode and is ignored while the mod's own message box is open. If a
  conflict still appears, change the
  keys in `AnotherDay.Plugin.json` (no rebuild needed).
- **Audio devices.** `NAudio` opens the default playback device and the default
  microphone. Voice chat software that holds the microphone exclusively can make
  push-to-talk fail; the plugin reports it in the HUD instead of crashing.
- **Same-process natives.** Tasks are issued with `TASK_*` natives only, after a
  second validation pass, so LSPDFR/ELS behaviour controllers are not starved.

## Known limitations

- **Identity matching is heuristic.** GTA does not expose a persistent ped ID.
  Matching uses model hash + sex + appearance fingerprint + last-seen window and
  stores a confidence value. Re-identification in the same area scores ~0.97;
  a despawning ped that returns hours later may be treated as a different person
  rather than risk attaching one person's memories to another.
- **Spatialised audio is approximated.** `WaveOut` has no 3D bus; volume falls
  off with distance from the player instead of true positional audio.
- **World-event sensing is proximity-based.** Events seen by the plugin (gunfire,
  crashes, fights, police escalation, aiming, zone/weather change, deaths) are
  propagated by radius; NPCs outside the snapshot radius learn nothing.
- **Voice variety is bounded by the provider catalogue.** Each NPC gets a
  persistent assignment from the Gemini voice list with a stable style string;
  the same voice can be shared by several NPCs when the catalogue is exhausted.
- **Enhanced edition is unsupported.** Different executable, different hook.

## Environment items that could not be verified without the game running

- In-game rendering of the HUD status lines (dialogue itself is never drawn).
- Conversation animation quality: gestures come from the vanilla
  `gestures@m@standing@casual` / `gestures@f@standing@casual` dictionaries and
  expressions from the vanilla `facials@gen_*` clipsets. Both are requested and
  existence-checked at runtime, but how they look on each ped model is unverified.
- Lip movement uses `SET_FACIAL_IDLE_ANIM_OVERRIDE` with `mp_facial/mic_chatter`
  gated on live audio RMS; the resulting jaw motion has only been reasoned about,
  not watched in game.
- Actual microphone capture and playback mixing inside GTA.
- Natives used by `ActionExecutor` under a live world (the signatures compile and
  match SHVDN 3.7.0.201, but behaviour at runtime is unverified).
- Long-session performance with many NPCs.

These are listed as manual checks in [TESTING.md](TESTING.md).
