# Development log

**Project:** Another Day in GTA
**Date:** 2026-11-02
**Build:** plugin 0.1.0 (`net48`), service 0.1.0 (`net10.0` / win-x64, self-contained)
**Game:** GTA V Legacy 1.0.3889.0, ScriptHookV 3889, SHVDN 3.7.0.201

## Phase status

| Phase | Status | Evidence |
|---|---|---|
| 1 Foundation | Done | Environment verified, framework repaired, plugin loads path established, IPC + logging implemented. |
| 2 NPC identity | Done (unit + integration) | Deterministic ids, profiles, SQLite persistence, identity reconstruction across restart. |
| 3 Gemini integration | Implemented, offline-verified | Async client, structured JSON contract, validation, personality-aware prompts. Live calls untested (no key). |
| 4 Voice | Implemented, offline-verified | Push-to-talk capture, transcription path, per-NPC persistent voice assignment, TTS cache, sequential playback, local fallback. Live TTS untested (no key). |
| 5 Memory | Done (unit + integration) | Long/short-term memories, scoring, dedupe, relationships, persistence across restart. |
| 6 Autonomous social | Implemented | Scheduler, pair selection, turn order, group conversations, manual `Alt+Shift+7` trigger, cap on concurrent conversations. |
| 7 World awareness | Implemented | Gunshots, explosions, crashes, fights, deaths, police escalation, aiming, zone/weather changes; per-category reactions by personality and distance. |
| 8 Physical actions | Implemented, not run in-game | Whitelisted action registry plus second validation layer and native task mapping. |
| 9 Optimisation | Implemented | Tiers, snapshot caps, AI concurrency/budget limits, audio cache trimming, memory pruning. Long-session behaviour unmeasured. |
| 10 Release | Installer + docs done | `tools\install.ps1`, `tools\uninstall.ps1`, `tools\smoke-test.ps1`, README, architecture, compatibility, testing docs. In-game acceptance outstanding. |

## Files created

Shared: `Protocol.cs`, `Dtos.cs`.
Service: `Program.cs`, `AppPaths.cs`, `AppConfig.cs`, `ServiceState.cs`,
`Logging/Log.cs`, `Diagnostics/Metrics.cs`, `Data/Database.cs`,
`Domain/{Models,IdentityGenerator,VoiceCatalog,VoiceEngine,EmotionEngine,MemoryEngine,NpcRegistry}.cs`,
`Ai/{AiContracts,PromptBuilder,GeminiClient,GeminiProvider,OfflineProvider,AudioService,ActionRegistry,ConversationEngine}.cs`,
`Simulation/{WorldEventEngine,SocialEngine}.cs`, `Ipc/PipeServer.cs`,
`Security/SecretStore.cs`.
Plugin: `Main.cs`, `PluginConfig.cs`, `PluginLog.cs`, `Ipc/{PipeClient,ServiceLauncher}.cs`,
`Audio/{AudioPlayback,MicrophoneRecorder}.cs`, `Game/{PedCollector,ActionExecutor}.cs`.
Tests: `ProtocolTests`, `PersistenceTests`, `EmotionTests`, `ServiceEndToEndTests`.
Tooling/docs: `tools/{install,uninstall,smoke-test,preflight,diagnose}.ps1`, `README.md`, `docs/*`.

## Session instrumentation (added after the in-game checklist was prepared)

- `ConversationEngine` now writes one summary line per player conversation:
  `Conversation with <name> (<category>, <npc id>): <ai|offline>, <ms>, <n> memory item(s), <n> action(s), voice=<voice>, reply="..."`.
  It is the log evidence for checklist items 3, 4 and 6 without recording full transcripts.
- `tools/preflight.ps1` verifies game build, ScriptHookV, the SHVDN loader,
  plugin files, service install, runtime, token, database and logs before launch.
- `tools/diagnose.ps1` triages the latest session: checklist coverage, notable
  plugin/service events, grouped errors and warnings, ScriptHookV/SHVDN log
  state, crash reports, and rule-based findings with a concrete fix per finding.

## Live verification run (2026-11-02)

First real launch with SHVDN 3.7.0.201. Findings and fixes:

- **Fixed: the plugin wrote no log at all.** `PluginLog.Initialize` was never
  called, and every write silently no-op'd because the directory was null. The
  plugin now initializes the log from the resolved data directory in its
  constructor, and `PluginLog` defaults to `%LOCALAPPDATA%\AnotherDayInGTA\logs`
  so even a failing constructor leaves a record.
- **Fixed: `install.ps1` could not update a running service.** The copy failed
  with a file-in-use error while a game-launched service held its DLLs. The
  installer now stops `AnotherDay.Service` before installing.
- **Fixed: `diagnose.ps1` mixed sessions.** The service log accumulates across
  runs, so earlier sessions' identities and conversations were credited to the
  new one. Service lines are now scoped to the plugin session's start time.
- **Added:** `diagnose.ps1` reports other SHVDN mods that threw during load.

Observations from the run (all from logs, item 1 of the checklist): SHVDN loaded
`AnotherDay.Plugin.dll` as a 3.7.0 script, `Main` started, the HUD notification
fired, the plugin auto-started the service, the authenticated handshake
succeeded and the service reported `Game connected (game v1_0_3788_0, protocol
1)`. No crash, no tick errors, no database fallback. The session stayed at the
main menu, so no pedestrian was bound; items 2-13 remain untested.

Known issue outside this project: the SHVDN 3.7.0.201 install makes
`SpeedLimitShower` and `RadarZoom` throw on load (`Notification.PostTicker`
signature change, `ReflectionTypeLoadException`), and `EnableAllinteriors` was
already broken by a missing `LemonUI.SHVDN3.dll`. These are the user's other
mods; they do not affect this plugin.

## Build results

- `dotnet build src/AnotherDay.Plugin/AnotherDay.Plugin.csproj -c Release` — succeeded.
- `dotnet publish src/AnotherDay.Service -c Release -r win-x64 --self-contained true` — succeeded, 201 files.
- Warnings kept visible: `NU1903` (transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 advisory) and
  `CA1416` (Windows-only APIs: DPAPI, System.Speech) — both are by design on Windows;
  they are not suppressed.
- Obsolete-API warnings in `PedCollector`/`PipeClient` (`Player.WantedLevel`,
  `Game.Version`) are tolerated; `Game.FileVersion` is already used for handshakes.

## Test results

- `dotnet test` — 15/15 passed (offline provider).
- `tools\smoke-test.ps1` against the installed exe — handshake, 2 identities,
  offline conversation, "SMOKE TEST PASSED".
- Installer run end-to-end; plugin DLLs present in `<game>\scripts`, service in
  `%LOCALAPPDATA%\AnotherDayInGTA\service`, default plugin config written.

## Framework change made on this machine

`ScriptHookVDotNet3.dll` was 3.7.0.167 with no loader; the 3.7.0.201 nightly
(with `ScriptHookVDotNet-Loader.asi`) was installed. The previous assembly is at
`<game>\AnotherDayInGTA_backup_shvdn\ScriptHookVDotNet3.dll`. No other game files
were touched.

## Known issues

1. In-game behaviour (rendering, mic capture, playback, action natives) is
   unverified — the game was not launched.
2. Live Gemini calls are unverified; no API key exists in this environment.
3. Identity confidence for a returning ped after a long absence is ~0.97 in the
   same model/sex class and deliberately falls back to a new identity when the
   evidence is weak, to avoid attaching one person's memories to another.
4. Spatial audio is distance attenuation, not true 3D placement.
5. `NU1903` advisory for the SQLite native bundle; updating
   `Microsoft.Data.Sqlite`/`SQLitePCLRaw` should be done when a patched release
   is available (the API surface used here is stable).

## Fixes from the 2026-11-02 Story Mode session

1. **Service froze on its first snapshot (cause of the "AI: offline mode" HUD).**
   `Database.MarkDormantAsync` ran its `UPDATE`s while its own reader still held
   the shared connection semaphore, so the second `WaitAsync` on that same
   semaphore never returned. The gate was never released, every later database
   call blocked forever, no snapshot round-trip completed, and the plugin had no
   data to display. The reader now finishes and releases the gate before the
   updates. Regression test:
   `PersistenceTests.MarkDormantReleasesTheConnectionForLaterQueries`.
2. **Handshake could hang silently.** `PipeClient.EnsureConnected` used
   `GetAwaiter().GetResult()` on the async pipe, ignoring the caller's timeout and
   wedging the handle after the first frame; it also logged failures at debug
   level, which is off by default. The handshake is fully asynchronous now and
   failures are logged as throttled warnings.
3. **HUD.** The status line is one cached text element redrawn each tick instead
   of a new scaleform every 100 ms (that was the flicker), and it no longer says
   "offline mode" before the first snapshot arrives — it reports
   `AI: connected, waiting for first snapshot` instead. The real reason for
   offline mode is always "no Gemini key".
4. **Keys.** `Alt+Shift+8` chat, `Alt+Shift+9` talk, `Alt+Shift+7` social,
   `Alt+Shift+6` panel; existing `F8`/`F9`/`F10`/`F11` configs are migrated on
   load. The function keys clashed with Story Mode character switching and with
   RagePluginHook's F4 console.
5. `PipeServer` no longer opens the pipe with `PipeOptions.WriteThrough`.

## Next tasks

1. Launch the game and work through [TESTING.md](TESTING.md) checks 1–14.
2. Configure a Gemini key and verify live dialogue, TTS voices and
   transcription (`--set-key`).
3. Tune `BackgroundChatterPerMinute`, `SimulationDensity` and
   `MaxActiveConversations` against measured frame time in a busy area.
4. Collect logs from a long session and adjust memory pruning/importance
   thresholds if conversations drift.
5. Consider upgrading the SQLite native package once a patched version ships.

## Required user actions

- Launch GTA V Legacy and run the manual checklist.
- Provide a Gemini API key via `--set-key` if AI dialogue and voices are wanted.
- Report any input-key conflicts with existing mods so defaults can move.
- If the RagePluginHook console (F4) still does not open, that is RagePluginHook
  itself, not this mod: check `RagePluginHook.log` in the game folder for an
  injection failure against the installed game build.
