# Where the Gemini key goes

There is **no text box in the game** for the key, and the key is not stored in a
file you can type into: it is encrypted with Windows DPAPI for your user account,
so the only supported way to write it is the command below. That is why you could
not find a place to put it.

## One-click way

1. Open the project folder `C:\Users\MTG40\RE-WORK\AnotherDayInGTA`.
2. Double-click **`Set-Gemini-Key.cmd`**.
3. Paste the key and press Enter. Nothing is echoed while you type.
4. The window says `Done` and the path below is printed.

Close the game first if it is running; the service only reads the key at startup.

## Command-line way (same thing)

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --set-key
```

Non-interactive alternative if the key is already in a file:

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --set-key-file "C:\path\to\key.txt"
```

## Where it ends up

```
C:\Users\MTG40\AppData\Local\AnotherDayInGTA\gemini.key
```

That file is encrypted binary bytes. Do not create or edit it by hand — it will
be rejected and the mod will keep running offline. Delete it to remove the key.

An environment variable also works and takes precedence: set `GEMINI_API_KEY`
(win64) before launching the game.

## How to tell it worked

Ask the service directly — this sends one request to Google and reports only the
verdict:

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --check-key
```

Then confirm the live voice socket itself opens (this is the path every spoken
NPC line uses):

```powershell
& "$env:LOCALAPPDATA\AnotherDayInGTA\service\AnotherDay.Service.exe" --live-test
```

`Live setup accepted: models/gemini-3.8-live` means the Live API is reachable and
your account can use the model. With a 16 kHz mono WAV path it also streams that
audio as a player turn and reports how much native audio came back.

Both key families work: `AQ.` (what AI Studio issues now) and `AIza` (older
projects). The prefix says nothing about whether a key is live. `--check-key`
prints a short fingerprint such as `FB7BE6B1/53` (length and the first eight hex
characters of a hash, never the key) and tests the `x-goog-api-key` header and
the `?key=` query parameter separately. That makes the two failures that look
identical in the log distinguishable:

- **one style rejected, the other accepted** - a client problem, not the key;
- **both rejected, with a suspicious length or two dots in the fingerprint** - the
  stored value is not a single key (typically a key pasted twice, or a key plus
  its own tail). Re-run `--set-key` and paste it once. `--set-key` now strips
  quotes, whitespace, byte-order marks and non-ASCII characters, and refuses a
  value shaped like a doubled paste instead of storing it.

`Gemini API key is valid.` means you are done. `Gemini rejected the key: HTTP 401`
means Google refused it (wrong key, revoked key, or a key from a project without
the Generative Language API enabled) — the game shows `AI: offline - key rejected`
and every reply falls back to local templates until you store a new key.

You can also open `%LOCALAPPDATA%\AnotherDayInGTA\logs\service-<date>.log` and look
at the most recent start:

```
Gemini: configured
Gemini health check: reachable
```

If it still says `Gemini: not configured (offline behavior active)`, the key was
written for a different data directory — the same log prints `Data directory:`
on the line above, and it must match
`C:\Users\MTG40\AppData\Local\AnotherDayInGTA`.

## What needs the key

| Feature | Without a key |
|---|---|
| Live voice conversation (`P`) | Disabled — the HUD says the key is missing or rejected |
| NPC voices | Silently subtitled, no audio (there is no Windows fallback) |
| Background chatter, memories, relationships | Template lines, subtitled only |
| Identities, memory, relationships, social scenes | Work normally |
