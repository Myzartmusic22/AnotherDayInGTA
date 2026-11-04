# Another Day in GTA

An original, persistent AI social simulation for **GTA V Legacy**. Pedestrians,
police, gang members, shopkeepers and drivers get their own identity,
personality, emotions, memories, relationships and a distinct natural voice.
They talk to the player, to each other, and keep remembering afterwards.

Nothing here is a reskin of an existing mod: the game plugin, the local AI
service, the protocol and the engines are all written for this project.

## How it fits together

```
GTA V (ScriptHookV + ScriptHookVDotNet 3.7)
 └─ AnotherDay.Plugin.dll        observes peds, captures the mic, plays PCM, draws the HUD
        │  \\.\pipe\AnotherDayInGTA         versioned JSON frames (snapshots, actions, TTS lines)
        │  \\.\pipe\AnotherDayInGTA.voice   duplex audio: mic PCM up, native audio down
        ▼
 AnotherDay.Service.exe         .NET 10 background service, runs outside the game
        ├─ identity / personality / emotion / memory / relationship engines
        ├─ social simulation + world-event reasoning (tiered, budgeted)
        ├─ Gemini Live (BidiGenerateContent): native-audio conversations, one voice per NPC
        ├─ Gemini TTS for autonomous NPC-to-NPC lines (still Gemini audio, never SAPI)
        └─ SQLite: %LOCALAPPDATA%\AnotherDayInGTA\anotherday.db
```

The game thread never waits for the network or the database. Every request is
asynchronous and every AI answer is validated twice: once by the service before
it leaves, and again by the plugin before any native call.

## Requirements

| Item | Version |
|---|---|
| Game | GTA V **Legacy** build 1.0.3889.0 (Enhanced is not supported) |
| Script hook | ScriptHookV for build 3889 |
| Script framework | ScriptHookVDotNet **3.7.0.201** + `ScriptHookVDotNet-Loader.asi` |
| Runtime | .NET Framework 4.8 (plugin), .NET 10 (service, shipped self-contained) |

## Install

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

The installer builds both projects, copies the plugin into `<game>\scripts`,
publishes the service to `%LOCALAPPDATA%\AnotherDayInGTA\service`, and writes a
default `AnotherDay.Plugin.json`. Existing files it replaces are backed up as
`<name>.bak-<timestamp>`. It never touches original GTA files.

To use a different game folder:

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -GamePath "D:\Games\GTAV"
```

## Configure Gemini (optional but recommended)

Without a working key the mod runs in offline mode: hand-written behaviour,
memories and relationships still work, but dialogue is limited to local templates
and nobody speaks out loud; dialogue is audio-only in this build, so nothing is
shown on screen either. The robotic Windows
System.Speech voices are no longer used by default; set
`voice.useLocalFallbackVoice` to `true` in `config.json` if you want them back.

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --set-key
```

Then confirm Google actually accepts it — a stored key is not automatically a
valid one, and an expired or mistyped key silently drops the whole mod back to
offline mode:

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --check-key
```

Both key families work: `AQ.` (what AI Studio issues now) and `AIza` (older
projects) — the prefix says nothing about validity. `--check-key` prints a
fingerprint (length and eight hex characters of a hash, never the key) and tests
the header and query auth styles separately, so a value that was quoted, padded
or pasted twice is visible instead of looking like a dead key. `--set-key` strips
clipboard noise and refuses a value shaped like a doubled paste.

The key is encrypted with Windows DPAPI for the current user and stored as
`gemini.key`. It is never written to `config.json`, the database or logs. The
`GEMINI_API_KEY` / `GOOGLE_API_KEY` environment variables take precedence.

Easiest path on Windows: double-click **`Set-Gemini-Key.cmd`** in the project root
and paste the key. The same walkthrough, with the exact file location, is in
[docs/GEMINI-KEY.md](docs/GEMINI-KEY.md).

Where the key lives and how to tell it worked:

- File: `%LOCALAPPDATA%\AnotherDayInGTA\gemini.key` (encrypted, current user only).
- Environment variable (alternative): `GEMINI_API_KEY`, set before launching the
  game; it overrides the file.
- The service reads the key **at startup**, so set it while the game is closed.
  If you add it while the service is running, stop the service (it restarts with
  the game) or restart the game.
- Confirmation: the next service start logs `Gemini: configured`, followed by
  `Gemini health check: reachable` a few seconds later. If it stays on
  `not configured`, the key was written to a different data directory — check the
  `Data directory:` line in the same log.
- `--set-key-file <path>` does the same thing non-interactively if you keep the
  key in a file.

Models in use: `gemini-3.8-live` for spoken conversations (native audio, no
transcript-then-speak step), `gemini-3.8-flash` for background group dialogue,
`gemini-3.8-flash-tts` for autonomous NPC-to-NPC voices, `gemini-3.5-transcribe`
for the legacy one-shot transcription endpoint. Change them in
`%LOCALAPPDATA%\AnotherDayInGTA\config.json` (`live.model`, `gemini.*`).

Check the Live path end to end without launching the game:

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --live-test
```

It opens a real Live socket, prints whether the setup was accepted, and with an
optional 16 kHz mono WAV argument streams that audio as the player turn and
reports how many bytes of native audio came back.

## Controls

| Key | Action |
|---|---|
| `P` (press once) | Opens a conversation with the nearest NPC; after connection, speak naturally without holding the key |
| silence after you speak | Ends your turn automatically; the NPC answers, then the microphone resumes listening |
| hold `P` while they speak | Deliberate interruption: their audio stops and you can speak until you release `P` |
| `O` | End the conversation immediately; the NPC goes back to its own behaviour |
| say "goodbye", "take care", or "be safe" | The NPC gives a closing reply, then the conversation ends and they resume ambient behaviour |
| `Alt+Shift+7` | Start a spoken social scene between the two nearest NPCs |
| `Alt+Shift+6` | Toggle the status panel |

The microphone is forwarded only during your turn and pauses while the NPC's
audio plays; it stops when the conversation ends. There is no text chat any
more: every conversation is spoken. `P` is a single key with no modifier because
`Shift` is GTA V's sprint/weapon-wheel modifier; nothing else in Story Mode uses
`P`. The other keys keep chords so they cannot collide with GTA V's own bindings
(F8/F10 switch characters, F4 is RagePluginHook's console). Everything is rebindable in
`<game>\scripts\AnotherDay.Plugin.json` (a single key like `P`, or
`Alt`/`Ctrl`/`Shift` plus a key).

Ride requests currently receive dialogue only. Gemini Live returns transcripts
but does not provide a structured, validated agreement and destination action,
so this build does not board the player or drive an NPC vehicle based on speech.

Voice settings in the same file:

| Setting | Meaning |
|---|---|
| `micDevice` | Part of the recording device name, e.g. `Headset`. Empty = Windows default |
| `bargeInThreshold` | Mic RMS that counts as talking over an NPC (default `0.02`) |
| `conversationRangeMeters` | How close someone must be for `P` to reach them |
| `hangupKey` | Ends a conversation (default `O`) — separate from the talk key so releasing `P` never hangs up |
| `volume` | Master output volume (distance attenuation is applied on top) |

Keys, ranges, volume and density live in `<game>\scripts\AnotherDay.Plugin.json`.
Runtime settings (autonomy, voice, model limits) live in
`%LOCALAPPDATA%\AnotherDayInGTA\config.json` and can be changed over IPC with
`settings.update`.

## Playing it

The mod runs in **Story Mode**; it does nothing in GTA Online. Launch the game as
usual (through `RagePluginHook.exe` if you want LSPDFR loaded as well), then:

1. **Wait a few seconds.** The HUD line at the top left goes
   `AI: service starting` → `AI: connected, waiting for first snapshot` →
   `AI: online` (or `AI: offline mode`, which only means no Gemini key). Peds
   around you are given an identity as snapshots arrive.
2. **Check the panel once with `Alt+Shift+6`.** It lists known NPCs, tracked NPCs,
   active conversations, the service status and the data directory. Press it
   again to hide it.
3. **Talk to someone with `P`.** Stand within 30 m of a pedestrian (the range is
   `conversationRangeMeters`) and press `P`. The HUD shows `CONNECTING`, then
   `LIVE <name> - listening`. The pedestrian turns to face you and a Gemini Live
   session opens with that person's voice and persona. Speak normally: the mic
   streams continuously, silence-ending VAD decides when your turn is over, and
   the reply arrives as native audio through your speakers. Say a clear goodbye to
   let their closing line finish and end naturally, or press `O` to end
   immediately.
   The conversation stays open between questions: they hold their ground, keep
   facing you and wait while you think. Somebody in a car answers through the
   window and stays at the wheel. Nothing spoken is ever written on screen —
   the top-left line only says whether they are speaking or listening.
   If the HUD says *No one close enough to talk to*, no pedestrian has been
   tracked yet — walk around for a couple of seconds and retry.
4. **Interrupt them.** Just start talking while they speak: playback flushes
   immediately and the model hears you. Transcripts stay inside the service for
   memory and context; nothing is captioned on screen.

A conversation ends when you say a clear goodbye, press `O`, walk past roughly
45 m, or when something genuinely dangerous happens (gunfire, a fight). The
NPC stays focused while either person is speaking, then listens for your next
turn. Drifting apart, a passing siren or half a minute of thinking does not end
it.

## Animation and vehicles

Talking is not only audio:

- **Mouths follow the voice.** The NPC's jaw is driven by the level of the audio
  that is actually playing (Gemini's PCM, chunk by chunk), so it stops on the
  last word and stops instantly when you talk over them. Your own character's
  mouth follows the microphone the same way, and closes when you stop.
- **Bodies talk too.** NPCs gesture from the vanilla `gestures@...` sets while
  they speak and nod while they listen. Friendly people explain and open their
  hands, aggressive people point and dismiss, nervous people keep it small.
  Gestures are upper-body only, so walking and driving tasks are untouched.
- **Faces react.** Expressions come from the NPC's personality and current mood
  (`angry`, `nervous`, `warm`, `confident`), so the face matches the voice.
- **Vehicles are safe.** Getting into a car or onto a bicycle during a
  conversation ends it cleanly (`you got into a vehicle`) and hands back the
  NPC, its face, the attention lock and the audio. You can start a conversation
  while already seated; you just never get a pedestrian gesture while driving.
  Bicycles and trains never receive car-only vehicle tasks.

None of this touches Gemini, memories, voices or the audio-only rule: dialogue
is still heard, never written on screen.

## One conversation at a time

A conversation belongs to exactly one pedestrian, with its own id, its own
generation and its own lifetime:

- driving off, or simply walking beyond talking range, ends it
  (`you walked too far away`);
- if the person despawns or dies, or you die, it ends immediately;
- pressing **P** beside somebody else ends the old conversation and starts the
  new one, instead of leaving you stuck with the previous partner;
- anything that arrives late from the previous conversation is thrown away, so a
  delayed answer can never be spoken by the new person.

Voices are matched to the body you are looking at. The plugin asks the game for
the ped's model sex, and the service will only use a voice classified for that
sex (verified against Gemini's published voice table). If a stored assignment no
longer fits - written when the catalog had a wrong gender, for example - it is
replaced with a compatible one; memories, relationships and personality are
untouched.

NPC work roles are only stated when the game provides a role signal (for example
police, emergency services, a gang model, or a driver currently at the wheel).
An ordinary civilian is not randomly told they are a barista, construction
worker, or another specific job. Female characters are not given low-register
voice directions or low-register catalog voices; old incompatible assignments
are replaced without resetting their identity history. The game currently gives
the service clothing variation IDs, not readable outfit descriptions, so the
NPC is instructed not to invent details such as a vest or hard hat. Exact outfit
recognition needs a model/clothing-description map that this build does not yet
include.

The voice path needs a working Gemini key; the HUD shows `Live voice unavailable`
with the reason when it is missing or rejected.
5. **Let them talk to each other.** `Alt+Shift+7` starts a scene between the two
   nearest NPCs; with `autonomousEnabled` on, they also start conversations on
   their own, react to gunfire, police, crashes and so on. Each line is
   Gemini-generated in that NPC's own voice and played positionally (volume by
distance) — never a Windows voice.
6. **Come back later.** Identities, voices, memories and relationships persist in
   `%LOCALAPPDATA%\AnotherDayInGTA\anotherday.db`, so the same person remembers
   the earlier conversation after a restart.

Every session is written to `%LOCALAPPDATA%\AnotherDayInGTA\logs`;
`tools\diagnose.ps1` summarises it.

## Verify the installation

```powershell
powershell -ExecutionPolicy Bypass -File tools\preflight.ps1   # before launching the game
powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1  # service + IPC, outside the game
powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1    # after a play session
```

`preflight` verifies the game build, ScriptHookV, the SHVDN loader, plugin files,
service install and data directory. `smoke-test` starts the service, performs the
authenticated handshake, sends a snapshot, runs one conversation and prints the
NPC's line. `diagnose` maps the plugin and service logs of the last session to
the checklist in [docs/TESTING.md](docs/TESTING.md) and names a fix for each
problem it finds. Automated tests:

```powershell
dotnet test tests\AnotherDay.Tests\AnotherDay.Tests.csproj
```

## Data and privacy

Everything is local: `anotherday.db` (identities, memories, relationships,
voice assignments), `audio_cache\` (cached Gemini TTS lines for background
chatter), `logs\` (structured rolling logs with secrets redacted), `backups\`,
`crashes\`. Live microphone audio is streamed to Gemini while a conversation is
open and is never written to disk. Only the prompts needed for a line are sent
to Google; no machine identifiers, no file contents.
Delete `%LOCALAPPDATA%\AnotherDayInGTA` to reset the world.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1              # keeps memories
powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1 -RemoveData  # deletes them
```

## Documentation

- [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) — what was verified on this machine
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — components, protocol, tiers, safety
- [docs/TESTING.md](docs/TESTING.md) — automated coverage and the in-game checklist
- [docs/DEVELOPMENT_LOG.md](docs/DEVELOPMENT_LOG.md) — build/test history and open items

## Third-party components

ScriptHookV and ScriptHookVDotNet are runtime dependencies, downloaded by the
user and not redistributed here. Newtonsoft.Json (MIT), NAudio (MIT),
Microsoft.Data.Sqlite (MIT) and the Gemini API are used according to their own
licences. No code from other GTA mods is included.
