# Complete removal — Another Day in GTA, 3 November 2026

Scope: everything the project created, installed or deployed into the GTA V
Legacy installation (`F:\SteamLibrary\steamapps\common\Grand Theft Auto V`) and
its runtime data directory. Nothing was rebuilt, reinstalled or repaired.

Nothing was permanently destroyed: every removed file was **moved** to
`backups\uninstall-20261103\` in this repository (178 MB) so the removal can be
undone by copying back. Delete that folder to finish the job irreversibly.

## 1. Stopped and removed runtime components

| Component | Action |
|---|---|
| `AnotherDay.Service.exe` (PID 23008, running) | terminated before file removal |
| Named pipe `AnotherDayInGTA` | process-owned; closed with the service, no OS registration |
| Service log/DB/caches/audio/mic/backups (136 MB) | `%LOCALAPPDATA%\AnotherDayInGTA` moved out |

## 2. Files removed from the game directory

**`<game>\scripts\` — 232 files moved to `backups\uninstall-20261103\scripts\`**

* `AnotherDay.Plugin.dll`, `AnotherDay.Shared.dll` — the plugin assemblies
* `AnotherDay.Plugin.json` — plugin configuration (keys, ranges, audio settings)
* 35 × `AnotherDay.Plugin.dll.bak-*` and 35 × `AnotherDay.Shared.dll.bak-*` — the
  accumulated install backups (`083433` → `211025`)
* `NAudio.dll`, `NAudio.Core.dll`, `NAudio.Wasapi.dll`, `NAudio.WinMM.dll` plus
  35 `.bak-*` copies each — the WASAPI audio engine used for microphone capture
  and NPC voice playback
* `Newtonsoft.Json.dll` plus 35 `.bak-*` copies — JSON dependency of the plugin

**`<game>\AnotherDayInGTA_backup_shvdn\`** — moved to
`backups\uninstall-20261103\rootbackup\`. This folder held the pre-project
`ScriptHookVDotNet3.dll` (1 432 064 B, 21 Jun) that the project set aside when it
upgraded SHVDN for its own build.

**`%LOCALAPPDATA%\AnotherDayInGTA\`** — moved to
`backups\uninstall-20261103\data\AnotherDayInGTA\`:
`anotherday.db` (+ `-shm`, `-wal`), `service\` (host executable and its .NET
libraries), `config.json`, `gemini.key`, `gemini.key.broken-20261102151958`,
`ipc.token`, `logs\`, `audio_cache\`, `mic\`, `crashes\`, `backups\` (7 database
snapshots).

## 3. Restored / deliberately not restored

**`ScriptHookVDotNet3.dll`** was the only pre-existing game file the project
replaced that we have a verified backup for. The original (Jun 21,
`a0cce863988ec6aa17c11f92e2996e8c`) is preserved in the quarantine folder, but it
was **not** put back into the game: the project also replaced the rest of the
SHVDN package (`ScriptHookVDotNet.dll`, `ScriptHookVDotNet2.dll`,
`ScriptHookVDotNet-Loader.asi`, `.ini`, `.pdb`, all 2 Nov 05:47) and we have no
backups of those. Reinstating the older 3.dll alone would mix a 3.6-era runtime
with a 3.7 core and could break `EnableAllInteriors`, `Persistence`,
`iFruitAddon2` and `SpeedLimitShower`. The complete 3.7 set
(`7ebb56ddbc7321b89e288cc375fbe83b`, 1 436 672 B, identical to
`lib\ScriptHookVDotNet3.dll`) is therefore left in place; it is the state in
which your other SHVDN scripts were last verified loading (log 21:38).

If you want the Jun 21 version back, copy it from
`backups\uninstall-20261103\rootbackup\AnotherDayInGTA_backup_shvdn\` — but then
the rest of the SHVDN package needs to be brought back to a matching release.

No other pre-existing game or mod file was found to have been modified by the
project, so nothing else needed restoring.

## 4. Registrations

None existed. Verified: no `HKCU\...\Run` value, no scheduled task, no Startup
shortcut (only your `Ollama.lnk`), no shell extension, no service registration,
no loader ini entry. The plugin was loaded purely by ScriptHookVDotNet scanning
`scripts\`, which is why removing the DLLs is sufficient — nothing else can load
it.

## 5. Verification after cleanup

| Check | Result |
|---|---|
| Project files anywhere in the game tree (`*anotherday*`) | **0** |
| Project files in `scripts\` | none — 19 files remain, all pre-existing mods |
| Project processes running | none |
| `%LOCALAPPDATA%\AnotherDayInGTA` | gone |
| `scripts\` file count | 251 → 19 |
| Residual Gemini/`NAudio` in game root | only `NAudio*.dll` dated 2023-09-04 / 2026-05-03, i.e. pre-project copies belonging to another mod — kept |

Preserved and confirmed present: `plugins\LSPD First Response.dll`,
`plugins\LSPDFR\` (incl. `NPCI\` with `NPCI_GeminiPrompt.txt`,
`NPCI_VoiceProfiles.ini`, animations and audio), `plugins\LemonUI.SHVDN3.dll`,
`plugins\Newtonsoft.Json.dll`, `ScriptHookV.dll`, `dinput8.dll`,
`ScriptHookVDotNet-Loader.asi`, `ScriptHookVDotNet.dll`,
`ScriptHookVDotNet2.dll`, `ScriptHookVDotNet3.dll`, `EnableAllInteriors`,
`Persistence`, `RadarZoom`, `SpeedLimitShower`, `iFruitAddon2`, `Metadata`,
`TrainerV.asi`, `ELS.asi`, `Gears.asi`, `SimpleHUD.asi`, `dxgi.asi`,
`openCameraV.asi`, `HeapAdjuster.asi`, `PackfileLimitAdjuster.asi`.

## 6. Left untouched — uncertain or foreign

| Item | Why left |
|---|---|
| `plugins\LSPDFR\NPCI\NPCI_GeminiPrompt.txt` | belongs to the **original NPCI mod**, not this project; its name merely contains "Gemini". Deleting it would damage NPCI. |
| `<game>\scripts\LemonUI.SHVDN3.dll` (22 May 2025) | predates the project and `plugins\` already holds your own LemonUI copy; treated as pre-existing, left in place. |
| `<game>\NAudio*.dll` in the game root | dated 2023–2026, i.e. installed by another mod; the project's copies lived in `scripts\` and those were removed. |
| `ScriptHookVDotNet.log`, `ScriptHookV.log` | normal game-generated logs; they still mention `AnotherDay.Plugin.Main` from the 21:38 session and are regenerated on next launch. |
| `%LOCALAPPDATA%\AnotherDayInGTA`-adjacent items in other apps | none found. |

## 7. Reversal

Copy back from `backups\uninstall-20261103\`: `scripts\*` → `<game>\scripts\`,
`rootbackup\AnotherDayInGTA_backup_shvdn\` → `<game>\`, and
`data\AnotherDayInGTA\` → `%LOCALAPPDATA%\`. No rebuild is needed; the
quarantined DLLs are the exact binaries that were installed.
