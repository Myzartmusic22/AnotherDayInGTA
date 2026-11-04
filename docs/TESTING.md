# Testing

## Automated suite

```powershell
dotnet test tests\AnotherDay.Tests\AnotherDay.Tests.csproj
```

Result on 2026-11-03: **187 passed, 0 failed** (`net10.0`, win-x64). The suite runs
with `GEMINI_API_KEY` cleared, so it exercises the offline path only; the Live
protocol is covered by a scripted fake transport instead of a real socket.

| Test | What it proves |
|---|---|
| `ProtocolTests.EnvelopeRoundTripsThroughFrameCodec` | Framing, IDs and payloads survive a round trip. |
| `ProtocolTests.UnsupportedSchemaVersionIsRejected` | A version mismatch is refused before handling. |
| `ProtocolTests.OversizedFrameIsRejected` | The 16 MiB cap is enforced on write. |
| `PersistenceTests.IdentityIsDeterministicForTheSameFingerprint` | Same fingerprint → same `npc_` id; different trait → different id. |
| `PersistenceTests.GeneratedProfilesAreCoherentAndStable` | Names, occupation, background, traits and category are populated and identical across regenerations. |
| `PersistenceTests.ProfileEmotionMemoryAndVoiceSurviveReopen` | Profile, emotion, memory, relationship and voice assignment survive closing and reopening the database. |
| `PersistenceTests.DuplicateMemoriesAreDetectable` | The duplicate hash lookup finds the same memory and ignores unrelated text. |
| `EmotionTests.ThreatRaisesAngerAndFearAndDropsTrust` | Local emotional reaction to a threat. |
| `EmotionTests.ApologyImprovesTrustAndHappiness` | Positive repair path. |
| `EmotionTests.EmotionsDecayTowardPersonalityBaseline` | Decay pulls anger/stress/frustration back down. |
| `EmotionTests.FearfulLowConfidenceNpcPrefersToFlee` | Personality + state drive the flee decision. |
| `EmotionTests.ReactionToGunfireDependsOnDistanceAndPersonality` | Same event, different distance → different fear. |
| `ServiceEndToEndTests.IpcPipelineBindsIdentitiesAndAnswersOfflineConversations` | Real named pipe: handshake → snapshot (3 identities, voices) → conversation with dialogue → memory written → status. |
| `ServiceEndToEndTests.InvalidTokenIsRejected` | Wrong IPC token gets `unauthorized`. |
| `ServiceEndToEndTests.IdentitiesAndKnowNpcsPersistAcrossServiceRestart` | New service process on the same database recognises all three NPCs with the same ids and ≥0.9 confidence. |
| `LiveProtocolTests.SetupNestsGenerationConfigAndAsksForNativeAudio` | The setup frame asks for `AUDIO` modality, the NPC's voice, its persona and server VAD. |
| `LiveProtocolTests.FlatSetupFormKeepsModalitiesAtTheRoot` | The fallback setup shape (root-level `responseModalities`) stays correct. |
| `LiveProtocolTests.AudioFramesUseSixteenKhzPcm` | Microphone frames are 16 kHz mono PCM, as Live requires. |
| `LiveProtocolTests.ServerAudioIsDecodedAtTwentyFourKhz` | 24 kHz native audio in `inlineData` is decoded into playable bytes. |
| `LiveProtocolTests.InterruptionTranscriptsAndResumptionAreParsed` | `interrupted`, both transcripts, the resumption handle and `goAway` are surfaced. |
| `LiveProtocolTests.ErrorMessagesAreSurfacedNotSwallowed` | A 401 from the socket becomes a visible reason, not silence. |
| `LiveProtocolTests.ConnectCompletesSetupAndStreamsBothDirections` | Full handshake against a fake transport: setup → audio up → audio/interrupt/transcript down. |
| `LiveProtocolTests.PersonaCarriesIdentityMoodAndMemory` | The system instruction contains name, job, mood, weapon awareness, memories, prior turns and place. |
| `LiveProtocolTests.VoiceAssignmentIsStableAndNeverAFallbackVoice` | Voices are Gemini voices and survive a fresh engine/DB reopen. |
| `LiveProtocolTests.VoiceChunksRoundTripThroughThePipePayload` | Mic buffers survive the base64 pipe payload unchanged. |
| `ConversationEngagementTests.StaysEngagedAfterTheFirstResponse` | Answering once does not end the conversation. |
| `ConversationEngagementTests.FarewellEndsOnlyAfterTheReplyAndItsAudioFinish` | A goodbye waits for completed reply audio before ending. |
| `ConversationEngagementTests.StaysEngagedThroughNaturalPauses` | Three minutes of thinking is not a hang-up. |
| `ConversationEngagementTests.EndsOnlyAfterTheConfiguredInactivityWindow` | Silence ends it only at the configured window. |
| `ConversationEngagementTests.PlayerSpeechResetsTheInactivityWindow` | Any speech restarts the clock. |
| `ConversationEngagementTests.HoldsAnOnFootNpcInPlace` | The face hold is re-issued on its own cadence, not every tick. |
| `ConversationEngagementTests.DoesNotWanderOffInsideTheLeaveRadius` | Drifting a few metres does not end it. |
| `ConversationEngagementTests.DriverKeepsTheVehicleForTheWholeConversation` | A driver is never given a task that leaves the car. |
| `ConversationEngagementTests.PassengerKeepsTheVehicleToo` | A passenger keeps the seat and the vehicle handle. |
| `ConversationEngagementTests.PlayerKeepsTalkingWithoutStartingAgain` | One `P` covers every follow-up question. |
| `ConversationEngagementTests.EndsWhenThePlayerWalksAway` | Distance, not silence, ends it. |
| `ConversationEngagementTests.EmergencyInterruptsTheConversation` | Real danger still wins. |
| `ConversationEngagementTests.DeathAndDisappearanceEndTheConversation` | Death and despawn are legitimate endings. |
| `ConversationEngagementTests.EndingStopsTheHoldAndRestoresNormalBehaviour` | The hold is released and the ped is free again. |
| `ConversationEngagementTests.AnEndedConversationDoesNotEndTwice` | An ended engagement is settled, not re-torn-down. |
| `ConversationEngagementTests.SpokenDialogueNeverAppearsOnScreen` | Captions and `You:` lines are gone for good. |
| `ConversationEngagementTests.LivePersonaRequiresEnglishAndStaysInTheConversation` | The Live persona pins English and in-conversation behaviour while keeping personality. |
| `ConversationEngagementTests.LivePersonaIncludesRoadAndNearbyVehicleAwareness` | The Live persona receives the measured road and nearby-vehicle context. |
| `ConversationEngagementTests.ConversationTargetPolicyLeavesRunningOrThreatenedPedsAlone` | Fleeing, running, sprinting, and fighting peds are not held for a conversation. |
| `ConversationEngagementTests.GeneratedVoiceStyleUsesRoleAndPlaceNotDemographicAccentLabels` | Voice style follows role and grounded location, not demographic labels. |
| `ConversationEngagementTests.IdentityGenerationDoesNotAssignAccentsFromRace` | Generated accents do not come from race-coded model names. |
| `ConversationEngagementTests.OrdinaryCivilianIsNotAssignedAnUnobservedJob` | An ordinary civilian is not assigned a random specific occupation. |
| `ConversationEngagementTests.OldRandomCivilianJobIsReplacedByGroundedIdentity` | Previously saved random civilian occupations are corrected. |
| `ConversationEngagementTests.GangArchetypeDoesNotInventASpecificGangJob` | Gang-affiliated pedestrians get a grounded group role, not an invented job. |
| `ConversationEngagementTests.OldSpecificGangJobIsReplacedWhenGangRoleIsObserved` | Previously saved specific gang jobs are corrected to the observed role. |
| `PersistenceTests.ReturningCivilianPedRepairsPreviouslyGeneratedJobGuess` | Previously saved wrong job claims are corrected in the NPC database. |
| `VoiceGenderTests.ConfidentFemaleIdentityDoesNotGenerateLowPitchDirections` | Confidence does not instruct a woman's voice to sound low. |
| `VoiceGenderTests.StoredLowRegisterFemaleVoiceIsReassigned` | A saved low-register voice is replaced for a female character. |
| `ConversationEngagementTests.TheEnglishRuleForbidsSwitchingLanguages` | The shared rule forbids drifting into another language. |
| `ConversationAnimationTests.MouthOpensWhileNpcAudioPlays` | NPC audio opens the mouth. |
| `ConversationAnimationTests.MouthClosesAsSoonAsTheAudioStops` | Silence closes it again, immediately. |
| `ConversationAnimationTests.PlayerMouthIgnoresMicNoiseBelowConfiguredSpeechThreshold` | Mic noise below the configured speech threshold does not animate the player. |
| `ConversationAnimationTests.NpcMouthIgnoresQuietPlaybackNoise` | Quiet output noise does not animate the NPC. |
| `PlaybackSpeechMonitorTests.SpeechIsActiveOnlyForTheCurrentRenderedAudioBlock` | NPC speech activity clears when the output provider renders silence. |
| `ConversationAnimationTests.PlayerMouthFollowsTheMicrophone` | The player's face follows own-voice detection. |
| `ConversationAnimationTests.OnlyTheCurrentSpeakerMovesTheirMouthWhenActivityOverlaps` | Overlapping activity never animates both speakers. |
| `ConversationAnimationTests.ListeningProducesNodsInsteadOfTalking` | Listening is a distinct, non-talking state. |
| `ConversationAnimationTests.GesturesAreNotSpammedEveryTick` | 15 s of speech yields a few gestures, not one per frame. |
| `ConversationAnimationTests.AggressiveAndNervousNpcsUseDifferentGestures` | Personality changes the gesture vocabulary. |
| `ConversationAnimationTests.RestrainedNpcsKeepTheirGesturesSmall` | Nervous NPCs avoid emphatic gestures. |
| `ConversationAnimationTests.ExpressionFollowsPersonality` | Anger, nerves, warmth and confidence map to expressions. |
| `ConversationAnimationTests.EndingStopsTheMouthAndAllGestures` | Nothing keeps animating after the conversation. |
| `AnimationSafetyTests.BodyAnimationIsRefusedInAnyVehicle` | No pedestrian body animation in a car or on a bicycle. |
| `AnimationSafetyTests.FaceAnimationIsStillAllowedInAVehicle` | Talking in a car is still animated. |
| `AnimationSafetyTests.DeadOrRagdolledPedsGetNoAnimationAtAll` | Dead and ragdolled peds are left alone. |
| `AnimationSafetyTests.PlayerGetsNoBodyAnimationWhileDrivingOrRiding` | The driving player never gets a conversation gesture. |
| `VehicleTransitionPolicyTests.EnteringACarDuringAConversationEndsIt` | Entering a vehicle ends the engagement by design. |
| `VehicleTransitionPolicyTests.ConversationsCanBeStartedFromACarOrBicycle` | Starting from a seat is allowed. |
| `GameThreadQueueTests.PostedWorkRunsInOrderOnDrain` | Background work executes on the game tick in order. |
| `GameThreadQueueTests.AFailingActionDoesNotStopTheRest` | One bad action cannot stall the queue. |
| `GameThreadQueueTests.DrainIsBoundedPerTick` | A flood cannot stall a frame. |
| `ConversationOwnershipTests.StartingAConversationCreatesAUniqueOwnedSession` | Every conversation gets its own id, identity and start time. |
| `ConversationOwnershipTests.ConversationPartnerIsProtectedFromTheMomentTheHandshakeStarts` | Ambient movement actions cannot override the selected NPC during connection setup. |
| `ConversationOwnershipTests.AGenerationThatIsNotLiveCannotClaimTheConversation` | Only the current generation owns the conversation. |
| `ConversationOwnershipTests.ReleasedSessionAcceptsNoFurtherCallbacks` | An ended conversation ignores late frames. |
| `ConversationOwnershipTests.DrivingAwayReleasesThePartner` | Leaving the interaction radius ends it. |
| `ConversationOwnershipTests.ADespawnedOrDeadPartnerIsReleased` | A missing partner ends it. |
| `ConversationOwnershipTests.SwitchingTargetEndsTheOldConversationBeforeTheNewOneStarts` | P beside someone else hands over cleanly. |
| `ConversationOwnershipTests.IdentityIsCheckedSoARecycledHandleIsNotEnough` | Ownership checks identity, not just the handle. |
| `ConversationOwnershipTests.PositionIsTrackedOnlyWhileTheConversationLives` | Last-known position stops updating after the end. |
| `VoiceGenderTests.SadachbiaIsMale` | The mislabelled voice is now classified correctly. |
| `VoiceGenderTests.TheServiceCatalogAgreesWithTheVerifiedTable` | The shipped catalog matches the verified table, all 30 voices. |
| `VoiceGenderTests.FemaleCharactersAreNeverOfferedAMaleVoice` | A female body can never receive a male voice. |
| `VoiceGenderTests.MaleCharactersAreNeverOfferedAFemaleVoice` | A male body can never receive a female voice. |
| `VoiceGenderTests.UnknownVoicesAreRejectedInsteadOfGuessed` | Unknown ids are refused rather than guessed. |
| `VoiceGenderTests.ResolutionFallsBackToAVerifiedVoiceOfTheRightSex` | A bad assignment resolves to a safe voice. |
| `VoiceGenderTests.AStoredAssignmentWithTheWrongSexIsCorrected` | A wrong stored assignment is corrected and persisted. |
| `VoiceGenderTests.AFemaleCharacterKeepsOneFemaleVoiceAcrossSessions` | Voice identities stay stable across sessions. |
| `LiveProtocolTests.NpcFarewellEndsAfterItsCompletedTranscriptAndOrdinaryRepliesDoNot` | The NPC can end with a spoken goodbye; ordinary replies do not. |

## Live voice checklist (needs a working key and the game)

Run `--check-key`, then `--live-test` first; both are outside the game and answer
the two questions that block everything else (is the key accepted, does a Live
session open and return audio).

1. HUD shows `AI: online (live voice)` and the panel's `Live voice: ready`.
2. `P` near a pedestrian → `CONNECTING` → `LIVE <name> - listening`.
3. Say "hey, how's your day going" → an audible reply in that NPC's voice.
4. Talk over the reply → audio stops mid-word and the NPC answers the new line.
5. **Ask three more questions without pressing `P`** → the NPC stays facing you,
   does not wander off, does not get into a car, and answers each one.
6. Wait ~30 s in silence, then speak again → still the same conversation and voice.
7. Talk to somebody sitting in a car → they answer through the window, do not get
   out, and do not drive off; ask for a ride and they accept or refuse in character.
8. Say "take care" → the NPC's closing reply plays fully, then they walk off
   and traffic resumes. `O` ends immediately; a later `P` starts a fresh talk.
9. Walk away past ~45 m → the session closes by itself.
10. Fire a gun → the conversation ends immediately.
11. Listen for Spanish or any other language → every reply is English.
12. Watch the screen while they talk → no subtitles, no `You:` line, no glyph boxes.
13. Two different pedestrians → two clearly different voices.
14. Restart the game, talk to the same person → same voice, and they recall the
    earlier conversation.
15. `Alt+Shift+7` → both NPCs speak their own lines audibly, in English.
16. Windows speech must never be heard: no line is played by SAPI.
17. **Watch the mouth while they talk.** It moves with the voice, stops on the
    last word without an extended delay, and stops instantly when you interrupt them.
18. **Watch the body.** They gesture while speaking, nod while listening, and a
    nervous ped gestures less than an aggressive one.
19. **Watch yourself.** Your own character's mouth moves while you speak and
    stops when you stop; you are never left in a talking face.
20. **Car stability.** Talking to somebody, then getting into a car: the game
    must not crash, the conversation ends with `you got into a vehicle`, the NPC
    walks off and traffic resumes.
21. **Bicycle stability.** Ride a bicycle with the microphone enabled and talk
    to somebody: no crash, no bicycle control loss, no pedestrian animation on
    the rider.
22. **Start from a seat.** Talk to somebody while sitting in a car: audio and
    microphone work, and no upper-body gesture is played on either of you.
23. **Exit and talk again.** Leave the car, press `P` on somebody else: a new
    conversation starts normally with no stale face or task from the last one.
24. **Ownership.** Talk to a pedestrian, drive several blocks, then press `P`
    beside a different one: the first conversation must be over (no line from
    the old partner) and the new one must answer you.
25. **Female voice.** Talk to a clearly female ped: the voice that answers must
    sound female, and the HUD line names the voice and the sex.
26. **Male voice.** Repeat with a male ped: the voice must sound male.
27. **Acknowledgement.** On starting, the ped turns to face you and stays
    attentive while you speak, rather than answering with their back turned.
28. **Moving targets.** Press `P` while the nearest pedestrian is running away
    or fighting. They should keep their current behavior; a nearby idle pedestrian
    should be selected instead, or the plugin should explain that nobody is
    available.
29. **Street awareness.** Start a conversation with a ped on a road and vehicles
    within roughly 12 m. Tell them they are blocking traffic; their response
    should acknowledge the scene rather than inventing unrelated surroundings.
30. **Role voice.** Compare a police officer, business worker, mechanic/driver,
    and gang-affiliated ped. Their word choice and delivery should reflect role
    and personality; the same individual should keep the same assigned Gemini
    voice. Do not expect an exact copy of GTA's prerecorded voice.
31. **Natural goodbye.** Say "take care" or "be safe" and listen for the NPC's
    closing line in full before they resume ambient behavior. Also let an NPC
    finish a clear goodbye; the conversation should release after its audio
    drains. Interrupting the closing line with `P` should keep the conversation
    open.
32. **Grounded occupation and appearance.** Ask an ordinary civilian about their
    work or clothes. They should not invent being a barista or construction
    worker, or claim a vest/hard hat unless an observed role or outfit actually
    establishes it. Repeat with a confident female ped; her voice should not be
    pushed into a low register.

## Shipped-binary smoke test

```powershell
powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1
```

Verified on 2026-11-02 against the installed `AnotherDay.Service.exe`:

```
==> Handshake
    connected, aiConfigured=False
==> Snapshot
    bindings=2 known=2
==> Conversation (offline or configured Gemini)
    Diego Kim [Algieba]: Morning.
    offlineFallback=True latency=30ms
SMOKE TEST PASSED
```

## Session tools

Before launching:

```powershell
powershell -ExecutionPolicy Bypass -File tools\preflight.ps1
```

Checks the game build, ScriptHookV, the SHVDN loader, plugin files, service
install, .NET runtime, IPC token, database and log directory. Exit code 1 means
the mod cannot load as installed.

After a session:

```powershell
powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1
```

Prints the checklist items the logs actually evidence, the notable plugin and
service events, grouped errors/warnings, the ScriptHookV and SHVDN log state,
any crash reports, and rule-based findings with a fix for each. Exit code 1
means a critical issue was found (plugin never loaded, no service connection,
database fallback, crash reports).

## Manual in-game checklist (requires the game)

These cannot be executed without launching GTA V. Each maps to an acceptance
criterion; all start with Story Mode, not Online.

| # | Check | Expected |
|---|---|---|
| 1 | Launch with the plugin installed | No crash; `Another Day in GTA loaded` notification and the HUD line: `AI: connected, waiting for first snapshot`, then `AI: online`/`AI: offline mode` once snapshots arrive. **Verified 2026-11-02** (main menu, see below). |
| 2 | Walk near pedestrians | HUD counts rise; `<data>\logs\*.log` shows identities assigned. |
| 3 | Press `P` once near a pedestrian | A spoken conversation opens; speak naturally without holding the key. |
| 4 | Ask two different NPCs the same question | Different wording, tone and voice. |
| 5 | Insult an NPC with a high-aggression profile | Refusal/disengagement, not agreement. |
| 6 | Speak after the connection is ready, then pause | The NPC answers in its assigned voice; the mic resumes after the response. |
| 7 | Speak again after the response without pressing `P` | The next turn is captured and answered hands-free. |
| 8 | Hold `P` while the NPC is answering, speak, release | The NPC stops speaking; the interrupted turn is submitted on release. |
| 9 | Quit to desktop, relaunch, return to the same spot | Same voice, and (if Gemini is configured) the NPC references the earlier exchange. |
| 10 | Press `Alt+Shift+7` near two NPCs | They talk to each other, one speaker at a time. |
| 11 | Fire a weapon near different NPC types | Police/gang/civilian react differently per personality and distance. |
| 12 | Pull a gun on a timid NPC | Flee action executes and completes without stuck tasks. |
| 13 | Disconnect the network during a conversation | Game stays responsive; offline lines continue; HUD reports offline mode. |
| 14 | Watch for 10+ minutes in a busy area | No frame-time collapse, no duplicate/overlapping voices. |
| 15 | Inspect `%LOCALAPPDATA%\AnotherDayInGTA` | Database, cache and logs present; no API key in logs. |

After each session run `tools\diagnose.ps1` and keep its output: it records which
items the logs evidence so "not tested" and "failed" stay distinguishable.

## Live run record (2026-11-02, item 1)

First launch with SHVDN 3.7.0.201 and the plugin installed, observed from the logs:

```
ScriptHookV.log   Registering script 'ScriptHookVDotNet-Loader.asi'
ScriptHookVDotNet.log  Loading assembly AnotherDay.Plugin.dll ...
                       Found 1 script(s) ... resolved to API version 3.7.0
                       Instantiating script AnotherDay.Plugin.Main ... Started script AnotherDay.Plugin.Main.
plugin-20261102.log  Another Day in GTA plugin loaded.
                     Started AI service (PID 2844).
                     Connected to AI service 0.1.0 at ...\AnotherDayInGTA
                     AI service connected (offline mode: no API key).
service-...log        IPC listening on pipe \\.\pipe\AnotherDayInGTA
                     Game connected (game v1_0_3788_0, protocol 1)
```

No crash, no plugin tick errors, no database fallback. This covered the load and
connect paths only: the session ended at the main menu, so no pedestrian was
bound and no key was pressed. Items 2-15 still need Story Mode.

### Third-party mods that fail on the same launch (not this project)

`ScriptHookVDotNet.log` records `EnableAllinteriors` (missing `LemonUI.SHVDN3`),
`RadarZoom` (`ReflectionTypeLoadException`) and `SpeedLimitShower`
(`Notification.PostTicker` signature changed in the installed SHVDN).
`tools\diagnose.ps1` reports these as a separate warning so they are not
mistaken for this mod failing.

## Not verified

## In-game checklist: hold, acknowledgement, release

These are the checks for the hold rework (see
`docs/CONVERSATION-ANIMATION.md` §6). They need Story Mode; nothing here can be
proven by the offline suite.

| # | Do this | Pass looks like |
|---|---|---|
| H1 | Walk up to a pedestrian who is walking away, press `P` | They stop on the press, not a second later. |
| H2 | Stay silent for ten seconds | They stay put and keep facing you; no wandering, no walking away. |
| H3 | Step to their side and behind them | Body turns back to you, head stays on you. |
| H4 | Talk for a minute | Mouth moves with the audio only; gestures, nods and expressions appear; they never walk off mid-sentence. |
| H5 | Press `O`, then watch the same pedestrian for a minute | They resume ordinary walking and street behaviour; no frozen ped left standing. |
| H6 | Talk to someone in a car | Driver stays at the wheel, head turns to you, no leave-vehicle task, no crash. |
| H7 | Repeat H1–H5 with a male and a female pedestrian | Same behaviour, voice matches the body. |
| H8 | Walk away mid-conversation | Conversation ends, they walk off on their own again. |

- Rendering, key handling and audio inside the running game.
- The exact `TASK_*` natives in a live world (they compile against SHVDN
  3.7.0.201; runtime behaviour is untested).
- Live Gemini text/TTS/transcription calls, because no API key was configured
  in this environment. Everything exercised above used the offline provider.
- Long-session performance under real population density.

## Hands-free conversation checklist (P)

Press `P` once to start a conversation. It keeps listening between turns while
the conversation is active; `O` ends it manually, and the normal lifecycle can
also end it. Holding `P` while the NPC speaks is an optional deliberate
interruption.

| # | Do this | Expect |
|---|---------|--------|
| P1 | Approach a walking pedestrian and tap `P` once | They stop and turn while the voice session opens; the mic starts when ready |
| P2 | Speak a full sentence, then pause for at least 1.1 seconds | The NPC answers after the pause; the mic is not forwarded during its audio |
| P3 | After the answer ends, speak again without pressing `P` | The second turn is captured and answered automatically |
| P4 | Stay silent for two minutes | No empty turn is sent; the conversation remains ready and the mic stays open |
| P5 | Hold `P` while the NPC is answering, speak, then release | The NPC is interrupted; release submits the new turn |
| P6 | Press `O` | Conversation ends, the pedestrian walks off and behaves normally |
| P7 | Talk to someone in a car | They keep driving; no task is ever put on the vehicle and they never get out for you |
| P8 | After several conversations, watch the same street | Peds still enter and exit vehicles and react to traffic; nothing is pinned |

Anything that fails here is a report, not a pass: H1–H8 above still apply to facing and release.


## Conversation lifecycle and goodbye (2026-11-02)

| # | Do this | Expect |
|---|---------|--------|
| L1 | Talk to a pedestrian, then drive across the map | Conversation ends; pressing `P` beside a new pedestrian selects them |
| L2 | Talk to A, then walk to B and hold `P` | A is released first; B becomes the partner with a new voice |
| L3 | Talk to someone, then die | The conversation ends during death; no stale target after respawn |
| L4 | Respawn at the hospital, talk to a new pedestrian | New conversation opens without restarting GTA V or the service |
| L5 | Speak "All right, you be safe" or "take care of yourself out there" | The conversation ends like pressing `O`; the NPC resumes walking and `P` works on the next person |
| L6 | Say "Thank you" mid-conversation | NPC answers normally; the conversation does not end |
| L7 | Talk to a passenger through the window | They still drive; no enter/leave task is applied |
| L8 | Watch a conversation partner | They stop, body and head turn to you, mouth moves with their audio, gestures appear |

If L1 or L2 fails, capture `logs/plugin-*.log` — the teardown reason is logged
and the owner phase should show `Idle` after the press.
