import io

p = 'src/AnotherDay.Plugin/Main.cs'
s = io.open(p, encoding='utf-8').read()
done = []


def rep(old, new, tag):
    global s
    assert old in s, 'ANCHOR MISSING: ' + tag
    s = s.replace(old, new, 1)
    done.append(tag)


rep("""        // Push-to-talk. The microphone is only forwarded to Gemini while the
        // talk key is held, which is what makes releasing it a finished turn
        // rather than a pause the model can talk over.
        private volatile bool _pushToTalkHeld;
        private volatile bool _pttHasSpeech;""",
"""        // The talk key as one explicit state: the first press opens the
        // conversation and the microphone, later presses are push-to-talk.
        // Every transition lives in AnotherDay.Shared/TalkStateMachine.cs and is
        // unit-tested; the plugin only executes the action it returns.
        private readonly TalkStateMachine _talk = new TalkStateMachine();
        private readonly object _talkSync = new object();

        // True once real speech was measured in the turn currently in flight.
        // The audio thread sets it; the game thread consumes it.
        private volatile bool _pttHasSpeech;

        // The microphone may only be forwarded while the machine is listening
        // and our own playback is silent, so the NPC's voice from the speakers
        // is never sent back to Gemini as if the player had said it.
        private bool MicOpen => _talk.ShouldStream && !_livePlayer.IsPlaying;""", 'fields')

rep("""            if (!_liveActive && !_liveStarting)
            {
                // Nobody eligible means no conversation and no microphone
                // session. The talk key is not marked held either, so the next
                // press is a fresh attempt rather than a release of nothing.
                if (!StartVoiceConversation()) return;
            }
            else
            {
                // Pressing the talk key over an NPC who is speaking is an
                // interruption the player asked for: their audio stops on this
                // frame and the new turn is what they answer.
                _livePlayer.Flush();
                Interlocked.Exchange(ref _npcAudioTicks, 0);
                Interlocked.Exchange(ref _npcLevel, 0f);
            }

            _pushToTalkHeld = true;
            _pttHasSpeech = false;""",
"""            DateTime now = DateTime.UtcNow;
            TalkAction action;
            lock (_talkSync) action = _talk.Press(now);

            if (action == TalkAction.StartConversation)
            {
                // The press itself opens the conversation: no second press is
                // needed. If nobody is in range the machine returns to idle so
                // the next press is a fresh attempt, not a release of nothing.
                if (!StartVoiceConversation())
                {
                    lock (_talkSync) _talk.Failed();
                    return;
                }
            }
            else if (action == TalkAction.BeginTurn)
            {
                // Pressing the talk key over an NPC who is speaking is an
                // interruption the player asked for: their audio stops on this
                // frame and the new turn is what they answer.
                _livePlayer.Flush();
                Interlocked.Exchange(ref _npcAudioTicks, 0);
                Interlocked.Exchange(ref _npcLevel, 0f);
            }

            _pttHasSpeech = false;""", 'press')

rep("""        private void EndPushToTalk()
        {
            if (!_pushToTalkHeld) return;
            _pushToTalkHeld = false;

            if (!_liveReady) return;

            VoiceLink link = _voice;
            if (link != null && link.IsConnected) link.SendTurnEnd();
            if (_pttHasSpeech) _engagement?.NotePlayerSpeech(DateTime.UtcNow);
        }""",
"""        private void EndPushToTalk()
        {
            DateTime now = DateTime.UtcNow;
            TalkAction action;
            lock (_talkSync) action = _talk.Release(now);
            if (action != TalkAction.SubmitTurn) return;
            SubmitTurn(now);
        }

        /// <summary>
        /// Sends the finished sentence as one turn. Release must never cut the
        /// NPC's answer short, so nothing here stops playback or the session.
        /// </summary>
        private void SubmitTurn(DateTime now)
        {
            VoiceLink link = _voice;
            if (link != null && link.IsConnected) link.SendTurnEnd();
            if (_pttHasSpeech) _engagement?.NotePlayerSpeech(now);
            _pttHasSpeech = false;
        }

        /// <summary>
        /// Runs once per frame on the game thread: feeds real microphone speech
        /// into the machine, ends an automatic turn on silence, and returns to
        /// "waiting for the key" once the NPC has finished answering.
        /// </summary>
        private void UpdateTalk()
        {
            if (!_liveActive || !_liveReady) return;

            DateTime now = DateTime.UtcNow;
            TalkAction action;
            lock (_talkSync)
            {
                if (_pttHasSpeech) _talk.NoteSpeech(now);
                action = _talk.Tick(now);
            }

            if (action == TalkAction.SubmitTurn) SubmitTurn(now);

            bool stillSpeaking = _livePlayer.IsPlaying ||
                now.Ticks < Interlocked.Read(ref _npcAudioTicks);
            if (stillSpeaking) return;

            lock (_talkSync)
            {
                if (_talk.Phase == TalkPhase.AwaitingReply) _talk.ReplyFinished();
            }
        }""", 'release+submit+tick')

rep("""                // Push-to-talk: the microphone reaches Gemini only while the
                // talk key is down, so a pause mid-sentence is not the end of
                // the player's turn and room noise is never a barge-in.
                if (!_pushToTalkHeld) return;""",
"""                // The microphone reaches Gemini only in a listening phase, and
                // never while our own playback is running (MicOpen).
                if (!MicOpen) return;""", 'mic gate')

rep("""            // Nothing is spoken over the player while they hold the talk key.
            // If the model answered a fragment the silence detector heard, that
            // answer is discarded; the real turn is answered after release.
            if (_pushToTalkHeld) return;""",
"""            // Nothing is spoken over the player while they hold the talk key.
            // If the model answered a fragment the silence detector heard, that
            // answer is discarded; the real turn is answered after release.
            if (_talk.Phase == TalkPhase.Holding) return;""", 'npc audio gate')

rep("""                    _farewellHardDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                    _pushToTalkHeld = false;""",
"""                    _farewellHardDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                    lock (_talkSync) _talk.Reset();""", 'farewell')

rep("""                _liveStarting = false;
                _pushToTalkHeld = false;
                _pttHasSpeech = false;""",
"""                _liveStarting = false;
                lock (_talkSync) _talk.Reset();
                _pttHasSpeech = false;""", 'teardown')

rep("""                _liveReady = true;
                _liveActive = true;
                _liveStarting = false;
                _liveError = null;""",
"""                _liveReady = true;
                _liveActive = true;
                _liveStarting = false;
                _liveError = null;

                // The handshake may have taken longer than the player's press,
                // so the first listening window opens here instead of on a key
                // that is already up.
                lock (_talkSync) _talk.Ready(DateTime.UtcNow);""", 'ready')

rep("""            UpdateConversationLifecycle(player);""",
"""            UpdateConversationLifecycle(player);
            UpdateTalk();""", 'tick hook')

rep("""                    (_pushToTalkHeld
                        ? " [holding " + _config.VoiceHotkey + "]"
                        : " [hold " + _config.VoiceHotkey + " to talk]");""",
"""                    (_talk.Phase == TalkPhase.Holding
                        ? " [holding " + _config.VoiceHotkey + "]"
                        : (_talk.ShouldStream
                            ? " [listening]"
                            : " [hold " + _config.VoiceHotkey + " to talk]"));""", 'hud')

io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('patched:', ', '.join(done))
print('remaining _pushToTalkHeld:', s.count('_pushToTalkHeld'))
