using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherDay.Plugin.Audio;
using AnotherDay.Plugin.Game;
using AnotherDay.Plugin.Ipc;
using AnotherDay.Shared;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json;
using Gta = GTA.Game;
using Screen = GTA.UI.Screen;
using TextElement = GTA.UI.TextElement;
using UiFont = GTA.UI.Font;

namespace AnotherDay.Plugin
{
    /// <summary>
    /// ScriptHookVDotNet entry point. Owns no intelligence: it observes the
    /// world, forwards snapshots over the named pipe, streams the microphone to
    /// the service's live voice pipe, plays Gemini's native audio back, and
    /// executes only the validated actions the service returns. Nothing here
    /// ever blocks the game thread on network or database work.
    /// </summary>
    public sealed class Main : Script
    {
        private sealed class PendingSpeech
        {
            public string Text;
            public string Speaker;
            public string AudioPath;
            public float Volume;
            public int SpeakerHandle;
            public DateTime DueUtc;
        }

        private PluginConfig _config;
        private PipeClient _pipe;
        private PedCollector _collector;
        private ActionExecutor _actions;
        private AudioPlayback _audio;
        private PcmPlayer _livePlayer;
        private MicStreamer _mic;

        private readonly Dictionary<int, PedBinding> _bindings = new Dictionary<int, PedBinding>();
        private readonly List<PendingSpeech> _speeches = new List<PendingSpeech>();
        private readonly object _stateSync = new object();

        private DateTime _nextSnapshotUtc = DateTime.MinValue;
        private DateTime _nextHandshakeUtc = DateTime.MinValue;
        private volatile bool _requestInFlight;
        private bool _showPanel;
        private bool _started;

        // ---- live voice conversation state. These are read on the game thread
        // and written on the pipe/audio threads, so they are volatile.
        private VoiceLink _voice;
        private volatile bool _liveStarting;
        private volatile bool _liveActive;
        private volatile bool _liveReady;
        private int _liveNpcHandle;
        private string _liveNpcId;
        private string _liveNpcName;
        private string _liveNpcLabel;
        private string _liveVoiceName;
        private string _liveError;
        private DateTime _liveErrorUntilUtc = DateTime.MinValue;
        private DateTime _nextLiveWatchdogUtc = DateTime.MinValue;
        private CancellationTokenSource _liveCts;

        // Ownership of the one conversation the player can be having. Every
        // callback from the voice pipe carries the generation it was opened
        // for, so a late answer from a pedestrian the player already left is
        // discarded instead of taking over the new conversation.
        private readonly ConversationOwner _owner = new ConversationOwner();

        // A spoken farewell is recognised by the service in the completed player
        // turn. The NPC's reply still plays; only then is everything released,
        // and the release is the same centralized teardown as every other end.
        private volatile bool _farewellPending;
        private volatile bool _farewellHeardReply;
        private DateTime _farewellDeadlineUtc = DateTime.MinValue;
        private DateTime _farewellHardDeadlineUtc = DateTime.MinValue;

        // Death and respawn are detected by the player ped disappearing or being
        // replaced, not by a distance check that cannot run while it is gone.
        private int _playerHandleLast;

        // The engagement state is the source of truth for "is this NPC still
        // talking to the player?"; the hold is what the game actually executes.
        private EngagementState _engagement;
        private ConversationHold _hold;

        // Every in-game effect requested by a background thread goes through
        // this queue and runs inside the script tick: calling a native from the
        // pipe or audio thread closes GTA V without a managed exception.
        private readonly GameThreadQueue _gameThread = new GameThreadQueue();
        private int _gameThreadId;
        private ConversationAnimator _animator;

        // Audio levels are measured where the audio actually is (capture and
        // pipe threads) and read by the game thread, never the other way round.
        private float _micLevel;
        private long _micVoiceTicks;
        private float _npcLevel;
        private long _npcAudioTicks;
        private bool _playerWasInVehicle;

        // The talk key as one explicit state: the first press opens the
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
        private bool MicOpen => _talk.ShouldStream && !_livePlayer.IsPlaying;

        private SettingsDto _settings = new SettingsDto();
        private PedSnapshotResponse _lastSnapshot;
        private TextElement _hudStatusText;
        private string _hudStatusKey;
        private TextElement _hudRecordingText;
        private string _hudRecordingKey;
        private TextElement _hudMessageText;
        private string _hudMessageKey;
        private readonly List<TextElement> _panelTexts = new List<TextElement>();
        private string _panelKey;
        private DateTime _nextLogicUtc = DateTime.MinValue;

        private string _lastMessage;
        private DateTime _lastMessageUntilUtc = DateTime.MinValue;

        public Main()
        {
            try
            {
                _config = PluginConfig.Load(AppDomain.CurrentDomain.BaseDirectory);
                PluginLog.Initialize(Path.Combine(_config.ResolvedDataDirectory, "logs"), _config.Debug);
                PluginLog.Info("Plugin config: data=" + _config.ResolvedDataDirectory + " pipe=" + _config.PipeName +
                    " keys=" + _config.VoiceHotkey + "/" + _config.SocialHotkey + "/" + _config.StatusHotkey +
                    " mic=" + (_config.MicDevice ?? "default"));
                _collector = new PedCollector();
                _actions = new ActionExecutor(ShowMessage)
                {
                    // The NPC being spoken to belongs to the conversation, not
                    // to ambient action commands, until the talk is over.
                    IsConversationPartner = handle => _liveActive && handle == _liveNpcHandle
                };
                _audio = new AudioPlayback();
                _audio.SetVolume(_config.Volume);
                _livePlayer = new PcmPlayer();
                _livePlayer.SetVolume(_config.Volume);
                _mic = new MicStreamer(_config.MicDevice);
                _mic.BufferAvailable += OnMicBuffer;
                GameThread.MarkCurrent();
                _gameThreadId = Thread.CurrentThread.ManagedThreadId;
                _pipe = new PipeClient(_config.PipeName, _config.TokenPath);

                // Draw every frame. Game text is immediate-mode: drawn for one
                // frame only, so a 100 ms tick left the HUD and subtitles missing
                // on most frames, which read as blinking. World work stays on its
                // own 100 ms cadence inside OnTick.
                Interval = 0;
                Tick += OnTick;
                KeyDown += OnKeyDown;
                KeyUp += OnKeyUp;
                Aborted += OnAborted;
                PluginLog.Info("Another Day in GTA plugin loaded.");
            }
            catch (Exception ex)
            {
                PluginLog.Error("Plugin failed to initialize: " + ex);
            }
        }

        // ---------------------------------------------------------------- tick

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (_config == null) return;
                GTA.Player player = Gta.Player;
                if (player == null || !player.Character.Exists())
                {
                    // The player ped vanishes between death and respawn. That is
                    // exactly when a conversation must not be left running: the
                    // old partner is not waiting at the hospital.
                    if (_liveActive || _liveStarting || !_owner.IsIdle)
                        StopVoiceConversation("player died", ConversationEndReason.PlayerDied);
                    return;
                }

                // Finish whatever the microphone, pipe and network threads asked
                // for before anything else this frame touches the world.
                _gameThread.Drain();

                // Lip-sync runs at frame rate, not on the 100 ms logic cadence:
                // mouth movement has to follow the audio chunks, not the AI.
                UpdateAnimator();

                DrawHud(player);
                if (DateTime.UtcNow < _nextLogicUtc) return;
                _nextLogicUtc = DateTime.UtcNow.AddMilliseconds(100);

                PumpSpeeches();
                UpdateBindings();
                UpdateLiveSession(player);

                if (!_started)
                {
                    _started = true;
                    ShowMessage("Another Day in GTA loaded. " + ControlsHint());
                }

                if (_settings != null && !_settings.MasterEnabled) return;

                if (!_pipe.IsConnected && DateTime.UtcNow >= _nextHandshakeUtc)
                {
                    _nextHandshakeUtc = DateTime.UtcNow.AddSeconds(5);
                    ServiceLauncher.TryStart(_config);
                    BeginHandshake();
                }

                if (_pipe.IsConnected && DateTime.UtcNow >= _nextSnapshotUtc && !_requestInFlight)
                    BeginSnapshot(player);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Tick failed: " + ex.Message);
            }
        }

        /// <summary>
        /// PipeClient performs the authenticated hello exchange when it first
        /// connects, so a benign ping is enough to bring the link up and learn
        /// the service settings.
        /// </summary>
        private async void BeginHandshake()
        {
            try
            {
                var request = new MessageEnvelope
                {
                    Type = MessageTypes.Ping,
                    PayloadJson = JsonConvert.SerializeObject(new PingDto { Echo = "plugin" }, PipeClient.JsonSettings)
                };

                await _pipe.SendAsync(request, new CancellationTokenSource(8000).Token).ConfigureAwait(false);
                HandshakeResponse hello = _pipe.Handshake;
                if (hello != null)
                {
                    if (hello.Settings != null) _settings = hello.Settings;
                    float volume = _config.Volume * Math.Min(1f, Math.Max(0f, _settings.Volume));
                    _audio.SetVolume(volume);
                    _livePlayer.SetVolume(volume);
                    PluginLog.Info("Connected to AI service " + hello.ServiceVersion + " at " + hello.DataDirectory);
                    ShowMessage("AI service connected" + (hello.LiveAvailable
                        ? " (live voice ready)."
                        : " (live voice unavailable: " + (hello.LiveReason ?? "no key") + ")."));
                }
            }
            catch (Exception ex)
            {
                LogLinkProblem("AI service link failed: " + ex.Message);
            }
        }

        private DateTime _nextSnapshotProblemUtc = DateTime.MinValue;
        private DateTime _nextLinkProblemUtc = DateTime.MinValue;

        private void LogLinkProblem(string message)
        {
            if (DateTime.UtcNow < _nextLinkProblemUtc) return;
            _nextLinkProblemUtc = DateTime.UtcNow.AddSeconds(30);
            PluginLog.Warn(message);
        }

        private void LogSnapshotProblem(string message)
        {
            if (DateTime.UtcNow < _nextSnapshotProblemUtc) return;
            _nextSnapshotProblemUtc = DateTime.UtcNow.AddSeconds(30);
            PluginLog.Warn(message);
        }

        private string ControlsHint()
        {
            return "hold " + _config.VoiceHotkey + " to talk | " + _config.HangupHotkey + " end | " +
                _config.SocialHotkey + " social | " + _config.StatusHotkey + " panel";
        }

        private async void BeginSnapshot(GTA.Player player)
        {
            _requestInFlight = true;
            _nextSnapshotUtc = DateTime.UtcNow.AddMilliseconds(_config.SnapshotIntervalMs);
            try
            {
                PedSnapshotRequest payload;
                try { payload = _collector.Collect(player, _config); }
                catch (Exception ex)
                {
                    LogSnapshotProblem("Snapshot collection failed: " + ex.Message);
                    return;
                }

                var request = new MessageEnvelope
                {
                    Type = MessageTypes.PedSnapshot,
                    PayloadJson = JsonConvert.SerializeObject(payload, PipeClient.JsonSettings)
                };

                MessageEnvelope raw = await _pipe.SendAsync(request, new CancellationTokenSource(20000).Token).ConfigureAwait(false);
                PedSnapshotResponse result = PipeClient.ParsePayload<PedSnapshotResponse>(raw);
                if (result == null) return;
                if (!result.Ok)
                {
                    PluginLog.Warn("Service rejected snapshot: " + result.Error);
                    return;
                }

                // The response arrives on a pool thread. Applying actions there
                // would call natives (task, vehicle, walk) off the game thread,
                // which is one of the ways GTA V closes without an exception, so
                // the whole application step is marshalled onto the script tick.
                _gameThread.Post(() => ApplySnapshot(result));
            }
            catch (Exception ex)
            {
                LogSnapshotProblem("Snapshot round-trip failed: " + ex.Message);
            }
            finally
            {
                _requestInFlight = false;
            }
        }

        /// <summary>Runs on the game thread: mirrors bindings, executes actions, queues speech.</summary>
        private void ApplySnapshot(PedSnapshotResponse result)
        {
            try
            {
                lock (_stateSync)
                {
                    _lastSnapshot = result;
                    _bindings.Clear();
                    foreach (PedBinding binding in result.Bindings ?? new List<PedBinding>())
                    {
                        if (binding != null) _bindings[binding.Handle] = binding;
                    }
                    _settings.AutonomousEnabled = result.AiAvailable;
                }

                foreach (ActionCommandDto command in result.Commands ?? new List<ActionCommandDto>())
                    _actions.Execute(command);

                foreach (NpcSpeechEvent speech in result.Speeches ?? new List<NpcSpeechEvent>())
                    QueueSpeech(speech);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Applying a snapshot failed: " + ex.Message);
            }
        }

        // ----------------------------------------------------------- gameplay

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (_config == null) return;

                // These events are raised by the keyboard hook's own thread,
                // not by the script tick. Selecting a pedestrian reads the
                // world, so every one of these actions is queued onto the game
                // thread; before this, selection refused to run at all and the
                // player was told nobody was nearby no matter who was standing
                // in front of them.
                if (_config.VoiceHotkey.Matches(e))
                {
                    e.SuppressKeyPress = true;
                    _gameThread.Post(BeginPushToTalk);
                }
                else if (_config.HangupHotkey.Matches(e))
                {
                    e.SuppressKeyPress = true;
                    _gameThread.Post(HangUp);
                }
                else if (_config.SocialHotkey.Matches(e))
                {
                    e.SuppressKeyPress = true;
                    _gameThread.Post(TriggerSocial);
                }
                else if (_config.StatusHotkey.Matches(e))
                {
                    e.SuppressKeyPress = true;
                    _gameThread.Post(() => _showPanel = !_showPanel);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Key handling failed: " + ex.Message);
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                if (_config == null) return;
                if (!_config.VoiceHotkey.MatchesKey(e)) return;
                e.SuppressKeyPress = true;
                // Same reason as the press: the key-up arrives off the script
                // tick, and ending the turn queues work that belongs to it.
                _gameThread.Post(EndPushToTalk);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Key release handling failed: " + ex.Message);
            }
        }

        private int NearestBinding(float maxDistance, int excludeHandle = 0)
        {
            Ped player = Gta.Player.Character;
            int best = 0;
            float bestDistance = maxDistance;
            lock (_stateSync)
            {
                foreach (KeyValuePair<int, PedBinding> pair in _bindings)
                {
                    if (pair.Key == excludeHandle) continue;
                    Ped ped = Entity.FromHandle(pair.Key) as Ped;
                    if (ped == null || !ped.Exists() || ped.IsDead) continue;
                    float distance = ped.Position.DistanceTo(player.Position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = pair.Key;
                    }
                }
            }
            return best;
        }

        // ------------------------------------------------------ live voice

        /// <summary>
        /// Push-to-talk press. Starts the conversation if there is none, and
        /// deliberately stops whatever the NPC is currently saying so the two of
        /// us are never talking at once.
        /// </summary>
        private void BeginPushToTalk()
        {
            if (_settings != null && !_settings.MasterEnabled)
            {
                ShowMessage("The mod is currently disabled.");
                return;
            }

            if (_farewellPending)
            {
                ShowMessage("You already said goodbye.");
                return;
            }

            // An unanswered handshake must never swallow the talk key: without
            // this, the first pedestrian keeps the conversation forever.
            if (_owner.ConnectingExpired(DateTime.UtcNow))
            {
                StopVoiceConversation(_liveError ?? "the voice service did not answer",
                    ConversationEndReason.SessionLost);
            }

            DateTime now = DateTime.UtcNow;
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

            _pttHasSpeech = false;
        }

        /// <summary>
        /// Push-to-talk release. This ends the player's turn, not the
        /// conversation: the service is told the sentence is finished so Gemini
        /// answers all of it, and nothing is cancelled.
        /// </summary>
        private void EndPushToTalk()
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
        }

        /// <summary>Explicit end of the conversation, separate from the talk key.</summary>
        private void HangUp()
        {
            if (_liveActive || _liveStarting) StopVoiceConversation("closed by player");
            else ShowMessage("No conversation to end.");
        }

        /// <summary>
        /// The conversation radius as it was before the interaction pass: the
        /// plugin's configured range. Facing, visibility and closeness ranking
        /// no longer narrow it.
        /// </summary>
        private float CurrentRange()
        {
            return Math.Max(5f, _config.ConversationRangeMeters);
        }

        private bool IsCurrentConversation(int generation)
        {
            return _owner.Owns(generation);
        }

        /// <param name="preferredHandle">The pedestrian the player actually
        /// turned to. Re-searching for the nearest one after releasing the old
        /// partner could pick the old partner again.</param>
        private bool StartVoiceConversation(int preferredHandle = 0)
        {
            if (_settings != null && !_settings.MasterEnabled)
            {
                ShowMessage("The mod is currently disabled.");
                return false;
            }
            if (_lastSnapshot != null && !_lastSnapshot.LiveAvailable)
            {
                ShowMessage("Live voice is unavailable: " + (_lastSnapshot.LiveReason ?? "no Gemini key") +
                    ". Run AnotherDay.Service.exe --set-key, then --check-key.");
                return false;
            }

            // Restored to the earlier working behaviour: the partner is the
            // nearest pedestrian the service has bound, within the configured
            // conversation range. No forward cone, no line-of-sight test and no
            // proximity bubble decide who the player is addressing.
            float range = CurrentRange();
            int handle = preferredHandle;
            if (handle != 0)
            {
                Ped preferred = Entity.FromHandle(handle) as Ped;
                if (preferred == null || !preferred.Exists() || preferred.IsDead ||
                    preferred.Position.DistanceTo(Gta.Player.Character.Position) > range)
                    handle = 0;
            }
            if (handle == 0) handle = NearestBinding(range);
            if (handle == 0)
            {
                ShowMessage("No one close enough to talk to.");
                return false;
            }

            Ped ped = Entity.FromHandle(handle) as Ped;
            if (ped == null || !ped.Exists()) return false;

            // A new partner may not inherit anything from the old one. The
            // previous conversation is fully invalidated first, then this one
            // starts from a clean slot with a fresh generation.
            if (!_owner.IsIdle)
            {
                StopVoiceConversation("you turned to someone else", ConversationEndReason.PlayerSwitchedTarget);
                if (!_owner.IsIdle)
                {
                    ShowMessage("Finishing the last conversation...");
                    return false;
                }
            }

            PedBinding binding;
            lock (_stateSync) _bindings.TryGetValue(handle, out binding);

            // The label the player sees belongs to this handle and to nothing
            // else. Before an identity exists it describes the model rather than
            // saying "someone", so the indicator is always useful.
            string label = binding != null && !string.IsNullOrWhiteSpace(binding.DisplayName)
                ? binding.DisplayName
                : "someone";

            ConversationSession session = _owner.Begin(handle, binding?.NpcId,
                label, PedGender.IsMale(ped),
                new Vec3(ped.Position.X, ped.Position.Y, ped.Position.Z), DateTime.UtcNow);
            if (session == null)
            {
                ShowMessage("Finishing the last conversation...");
                return false;
            }

            // Engage before the socket opens: the NPC has to be held from the
            // first sentence, or ambient wander wins the race.
            BeginEngagement(ped, handle, binding?.NpcId);
            _liveStarting = true;
            _liveError = null;
            _farewellPending = false;
            int generation = session.Generation;
            _playerHandleLast = Gta.Player.Character.Handle;

            var start = new VoiceStartRequest
            {
                NpcHandle = handle,
                NpcId = binding?.NpcId,
                PlayerHandle = Gta.Player.Character.Handle,
                ConversationId = session.ConversationId,
                NpcIsMale = session.IsMale,
                Zone = SafeZone(),
                Weather = SafeWeather(),
                ClockHour = Function.Call<int>(GTA.Native.Hash.GET_CLOCK_HOURS),
                PlayerArmed = Gta.Player.Character.Weapons.Current != null &&
                              Gta.Player.Character.Weapons.Current.Hash != WeaponHash.Unarmed,
                PlayerAiming = Gta.Player.IsAiming,
                NpcInVehicle = ped.IsInVehicle(),
                NpcIsDriver = ped.IsInVehicle() && ConversationHold.IsDriving(ped, ped.CurrentVehicle),
                NpcVehicleModel = SafeVehicleName(ped)
            };

            _liveNpcHandle = handle;
            _liveNpcId = binding?.NpcId;
            _liveNpcName = label;
            _liveNpcLabel = label;
            ShowMessage("Connecting to " + label + " (" + (session.IsMale ? "male" : "female") +
                " voice, " +
                ped.Position.DistanceTo(Gta.Player.Character.Position).ToString("0.0") + " m)...");

            // The player may start a conversation from a car or a bicycle; the
            // animator simply never gives them pedestrian body animation.
            _playerWasInVehicle = Gta.Player.Character.IsInVehicle();
            _animator = new ConversationAnimator();
            _animator.Attach(ped, ConversationStyle.Neutral);

            _liveCts = new CancellationTokenSource();
            CancellationToken token = _liveCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    string pipeName = Protocol.VoicePipeName(_config.PipeName);
                    VoiceLink link = await VoiceLink.ConnectAsync(pipeName, _config.TokenPath, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        link.Dispose();
                        return;
                    }

                    if (link.Hello != null && !link.Hello.LiveAvailable)
                    {
                        _liveError = link.Hello.Reason ?? "live voice is unavailable";
                        link.Dispose();
                        return;
                    }

                    if (!_owner.Owns(generation))
                    {
                        // The player ended this conversation while the socket
                        // was still connecting. Adopting the link here is what
                        // used to leave a Gemini session alive for a partner
                        // nobody was standing next to.
                        try { link.SendStop(); } catch { }
                        link.Dispose();
                        return;
                    }

                    link.Ready += ready => OnVoiceReady(generation, ready);
                    link.AudioReceived += chunk => OnLiveAudio(generation, chunk);
                    link.EventReceived += (kind, text) => OnVoiceEvent(generation, kind, text);
                    link.Closed += reason =>
                    {
                        if (_liveActive || _liveStarting)
                            OnVoiceEvent(generation, VoiceEventKinds.SessionEnded, reason);
                    };

                    _voice = link;
                    await link.SendStartAsync(start, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _liveError = ex.Message;
                    PluginLog.Warn("Live voice connect failed: " + ex.Message);
                    if (_owner.Owns(generation))
                        StopVoiceConversation("could not reach the voice service", ConversationEndReason.SessionLost);
                }
                finally
                {
                    // Only clear the flag for the conversation this connect was
                    // part of; a failed handshake must not disturb a newer one.
                    if (!_liveReady && _owner.Owns(generation)) _liveStarting = false;
                }
            });

            return true;
        }

        /// <summary>Raised on the voice pipe's reader thread. Marshal it.</summary>
        private void OnVoiceReady(int generation, VoiceReadyResponse ready)
        {
            _gameThread.Post(() => HandleVoiceReady(generation, ready));
        }

        private void HandleVoiceReady(int generation, VoiceReadyResponse ready)
        {
            try
            {
                if (!_owner.Owns(generation))
                {
                    PluginLog.Debug("Ignored a ready frame from an earlier conversation.");
                    return;
                }

                ConversationSession session = _owner.Current;
                if (session == null) return;

                // The service echoes the conversation it opened. A frame for a
                // different one must never become this conversation's voice.
                if (ready != null && !string.IsNullOrEmpty(ready.ConversationId) &&
                    !string.Equals(ready.ConversationId, session.ConversationId, StringComparison.Ordinal))
                {
                    PluginLog.Warn("Refusing a ready frame opened for another conversation.");
                    StopVoiceConversation("session mismatch", ConversationEndReason.SessionLost);
                    return;
                }

                if (ready != null && !string.IsNullOrEmpty(ready.NpcId) && !string.IsNullOrEmpty(session.NpcId) &&
                    !string.Equals(ready.NpcId, session.NpcId, StringComparison.OrdinalIgnoreCase))
                {
                    PluginLog.Warn("Refusing a session opened for " + ready.NpcId + " during " + session.NpcId + "'s conversation.");
                    StopVoiceConversation("session mismatch", ConversationEndReason.SessionLost);
                    return;
                }

                if (!_owner.MarkReady(generation))
                {
                    StopVoiceConversation("session mismatch", ConversationEndReason.SessionLost);
                    return;
                }

                _liveReady = true;
                _liveActive = true;
                _liveStarting = false;
                _liveError = null;

                // The handshake may have taken longer than the player's press,
                // so the first listening window opens here instead of on a key
                // that is already up.
                lock (_talkSync) _talk.Ready(DateTime.UtcNow);
                _engagement?.MarkReady(DateTime.UtcNow);
                if (ready != null)
                {
                    _liveNpcName = ready.DisplayName ?? _liveNpcName;
                    _liveVoiceName = ready.VoiceName;
                    _liveNpcId = ready.NpcId ?? _liveNpcId;

                    // A voice that does not match the body is the exact defect
                    // this pass exists to fix, so it is reported rather than
                    // passed off as normal.
                    if (!string.IsNullOrEmpty(ready.VoiceName) && !GeminiVoices.IsCompatible(ready.VoiceName, session.IsMale))
                        PluginLog.Warn("Voice " + ready.VoiceName + " does not match " +
                            (session.IsMale ? "male" : "female") + " " + session.DisplayName + ".");

                    // Gestures and facial expressions come from the same
                    // personality and mood that shaped the voice.
                    _animator?.SetStyle(ConversationStyle.FromDto(ready.Animation));
                }

                // Only one voice at a time: stop file-based chatter so the live
                // conversation is not talking over a queued line.
                lock (_stateSync) _speeches.Clear();
                _audio.Stop();

                if (!_mic.IsRunning && !_mic.Start())
                {
                    ShowMessage(_mic.LastError ?? "Microphone unavailable.");
                    StopVoiceConversation("microphone unavailable");
                    return;
                }

                ShowMessage("Talking to " + _liveNpcName + " (" + (_liveVoiceName ?? "live voice") + ", " +
                    (session.IsMale ? "male" : "female") + "). Hold " + _config.VoiceHotkey +
                    " to speak, release to let them answer, " + _config.HangupHotkey + " to end.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Voice ready handler failed: " + ex.Message);
            }
        }

        private void OnMicBuffer(byte[] pcm, float rms)
        {
            try
            {
                if (!_liveActive || !_liveReady) return;

                // The microphone reaches Gemini only in a listening phase, and
                // never while our own playback is running (MicOpen).
                if (!MicOpen) return;

                VoiceLink link = _voice;
                if (link == null || !link.IsConnected) return;

                // Real speech keeps the engagement alive even before the model
                // reports a transcript, so a thoughtful pause is not a hang-up.
                if (rms >= Math.Max(0.005f, _config.BargeInThreshold))
                {
                    _engagement?.NotePlayerSpeech(DateTime.UtcNow);
                    _pttHasSpeech = true;
                    Interlocked.Exchange(ref _micLevel, rms);
                    Interlocked.Exchange(ref _micVoiceTicks, DateTime.UtcNow.Ticks);
                }

                link.SendAudio(pcm);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Mic buffer forward failed: " + ex.Message);
            }
        }

        private void OnLiveAudio(int generation, byte[] pcm24k)
        {
            if (!IsCurrentConversation(generation)) return;
            if (!_liveActive) return;

            // Nothing is spoken over the player while they hold the talk key.
            // If the model answered a fragment the silence detector heard, that
            // answer is discarded; the real turn is answered after release.
            if (_talk.Phase == TalkPhase.Holding) return;

            // Pipe thread: measuring is cheap and touches no game state. The
            // mouth on the game thread reads these two values.
            float level = Level(pcm24k);
            Interlocked.Exchange(ref _npcLevel, level);
            if (level >= 0.012f)
            {
                double seconds = pcm24k.Length / 2.0 / 24000.0 + 0.08;
                Interlocked.Exchange(ref _npcAudioTicks,
                    DateTime.UtcNow.Ticks + (long)(seconds * TimeSpan.TicksPerSecond));
            }

            _livePlayer.Play(pcm24k);

            // A farewell is only over once the NPC has actually replied; a slow
            // first audio frame must not be mistaken for a finished goodbye.
            if (_farewellPending) _farewellHeardReply = true;
        }

        private static float Level(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length < 2) return 0f;
            int samples = pcm16.Length / 2;
            double sum = 0;
            for (int i = 0; i < samples; i++)
            {
                short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
                double value = sample / 32768.0;
                sum += value * value;
            }
            return (float)Math.Sqrt(sum / samples);
        }

        /// <summary>Raised on the voice pipe's reader thread. Marshal it.</summary>
        private void OnVoiceEvent(int generation, string kind, string text)
        {
            _gameThread.Post(() => HandleVoiceEvent(generation, kind, text));
        }

        private void HandleVoiceEvent(int generation, string kind, string text)
        {
            // A frame from a conversation that is over is not news about this one.
            if (!IsCurrentConversation(generation))
            {
                PluginLog.Debug("Ignored a " + kind + " event from an earlier conversation.");
                return;
            }

            switch (kind)
            {
                case VoiceEventKinds.Interrupted:
                    _livePlayer.Flush();
                    // The mouth closes on the same tick: an interrupted NPC must
                    // not keep mouthing words nobody can hear.
                    Interlocked.Exchange(ref _npcAudioTicks, 0);
                    Interlocked.Exchange(ref _npcLevel, 0f);
                    break;
                case VoiceEventKinds.InputTranscript:
                    // Audio only: the transcript lives in the service for memory
                    // and context, and never appears on screen.
                    if (!string.IsNullOrWhiteSpace(text)) _engagement?.NotePlayerSpeech(DateTime.UtcNow);
                    break;
                case VoiceEventKinds.OutputTranscript:
                    if (!string.IsNullOrWhiteSpace(text)) _engagement?.NoteNpcResponse(DateTime.UtcNow);
                    break;
                case VoiceEventKinds.TurnComplete:
                    _engagement?.NoteTurnComplete(DateTime.UtcNow);
                    break;
                case VoiceEventKinds.Farewell:
                    // Recognised in the completed player turn. The NPC's short
                    // farewell still plays; the centralized teardown runs once
                    // the audio has drained, releasing partner, mic and socket.
                    _farewellPending = true;
                    _farewellDeadlineUtc = DateTime.UtcNow.AddSeconds(1.5);
                    _farewellHardDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
                    lock (_talkSync) _talk.Reset();
                    PluginLog.Info("Player said goodbye; releasing after the farewell.");
                    break;
                case VoiceEventKinds.SessionEnded:
                case VoiceEventKinds.Closed:
                    if (_liveActive || _liveStarting) StopVoiceConversation(text ?? "session ended");
                    break;
                case VoiceEventKinds.Error:
                    _liveError = text;
                    _liveErrorUntilUtc = DateTime.UtcNow.AddSeconds(12);
                    PluginLog.Warn("Live voice error: " + text);
                    if (!_liveReady) StopVoiceConversation(text ?? "live voice error");
                    break;
            }
        }

        private void UpdateLiveSession(GTA.Player player)
        {
            // The lifecycle clock runs every tick, before any engagement logic.
            // It is the only code that can release a conversation, so it must
            // never sit behind a guard a broken handshake can satisfy.
            UpdateConversationLifecycle(player);
            UpdateTalk();

            if (!_liveActive) return;
            if (DateTime.UtcNow < _nextLiveWatchdogUtc) return;
            _nextLiveWatchdogUtc = DateTime.UtcNow.AddMilliseconds(350);

            Ped ped = Entity.FromHandle(_liveNpcHandle) as Ped;
            if (ped == null || !ped.Exists()) return;

            // A conversation is a pedestrian interaction. Getting into a car or
            // onto a bicycle ends it on the spot, with the hold, the face and
            // the audio torn down in a known order, instead of leaving a task on
            // a moving player or a stale vehicle reference behind.
            bool playerInVehicle = player.Character.IsInVehicle();
            if (VehicleTransitionPolicy.EndsConversationOnEntry(true,
                _playerWasInVehicle, playerInVehicle))
            {
                _playerWasInVehicle = playerInVehicle;
                StopVoiceConversation("you got into a vehicle", ConversationEndReason.PlayerEnteredVehicle);
                return;
            }
            _playerWasInVehicle = playerInVehicle;

            bool pedAlive = !ped.IsDead;
            double distance = ped.Position.DistanceTo(player.Character.Position);

            ConversationSession session = _owner.Current;
            if (session == null) return;
            if (pedAlive)
                session.NoteSeen(new Vec3(ped.Position.X, ped.Position.Y, ped.Position.Z), DateTime.UtcNow);

            // A lost hold or engagement is repaired, not treated as a reason to
            // stop watching the conversation.
            if (_hold == null && pedAlive) BeginEngagement(ped, _liveNpcHandle, _liveNpcId);

            EngagementDecision decision = _engagement != null
                ? _engagement.Evaluate(DateTime.UtcNow, distance, pedAlive, true,
                    IsEmergency(player.Character, ped))
                : EngagementDecision.Continue(EngagementHold.None);

            if (decision.Ends)
            {
                StopVoiceConversation(ReasonText(decision.Reason), decision.Reason);
                return;
            }

            // Track the vehicle context every tick so a driver who gets out on
            // their own (scripted event, crash) is not forced back in, and a
            // passenger is never mistaken for a driver.
            _engagement?.NoteVehicle(ped.IsInVehicle(), ped.IsInVehicle() && ConversationHold.IsDriving(ped, ped.CurrentVehicle),
                ped.CurrentVehicle?.Handle ?? 0);

            if (decision.Hold != EngagementHold.None) _hold?.Apply(decision.Hold);

            // Animation is driven by the audio that actually reached the
            // speakers, not by transcripts, so the mouth moves on the same
            // frames as the voice and stops the moment it does.
            // The voice comes from the pedestrian, not from the player's head:
            // gain follows distance and pan follows the side they stand on, both
            // smoothed inside the player so walking past them slides.
            float gain = 1f, pan = 0f;
            bool placed;
            SpatialFor(ped, out gain, out pan, out placed);
            _livePlayer.SetVolume(_config.Volume * Math.Min(1f, _settings?.Volume ?? 1f));
            _livePlayer.SetSpatial(placed ? gain : 1f, pan, DateTime.UtcNow);
        }

        /// <summary>
        /// Where a voice should sit in the stereo field, from the speaker's real
        /// position relative to the camera. A driving player hears a pedestrian
        /// slide past instead of a voice glued to the centre of the screen, and
        /// everything falls back to a centred full-level voice if the camera is
        /// unavailable, which is why the caller retries every tick.
        /// </summary>
        private void SpatialFor(Ped speaker, out float gain, out float pan, out bool placed)
        {
            gain = 1f;
            pan = 0f;
            placed = false;
            if (!_config.SpatialAudio) return;
            if (speaker == null || !speaker.Exists()) return;

            try
            {
                GTA.Math.Vector3 origin;
                GTA.Math.Vector3 right;

                GTA.Math.Vector3 forward;
                try
                {
                    origin = GTA.GameplayCamera.Position;
                    forward = GTA.GameplayCamera.Direction;
                }
                catch
                {
                    Ped player = GTA.Game.Player == null ? null : GTA.Game.Player.Character;
                    if (player == null || !player.Exists()) return;
                    origin = player.Position;
                    forward = player.ForwardVector;
                }

                right = GTA.Math.Vector3.Cross(forward, new GTA.Math.Vector3(0f, 0f, 1f));
                if (right.Length() < 0.001f) right = new GTA.Math.Vector3(1f, 0f, 0f);

                GTA.Math.Vector3 offset = speaker.Position - origin;
                float distance = offset.Length();
                float lateral = 0f;
                if (distance > 0.01f)
                {
                    GTA.Math.Vector3 direction = offset * (1f / distance);
                    lateral = GTA.Math.Vector3.Dot(direction, GTA.Math.Vector3.Normalize(right));
                }

                gain = AudioSpatial.Attenuation(distance,
                    _config.AudioNearDistanceMeters, _config.AudioFarDistanceMeters);
                pan = AudioSpatial.Pan(lateral, distance,
                    _config.AudioNearDistanceMeters, _config.AudioFarDistanceMeters);
                placed = true;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("Spatial placement failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The one lifecycle clock. Every reason a conversation can end is
        /// decided here, in order, and none of it depends on the engagement
        /// simulation: a dead player, a despawned partner or an unanswered
        /// handshake releases the session even if the richer state never ran.
        /// </summary>
        private void UpdateConversationLifecycle(GTA.Player player)
        {
            ConversationSession session = _owner.Current;
            if (session == null)
            {
                // Flags with no owner are the residue of an interrupted teardown.
                if (_liveActive || _liveStarting || _liveNpcHandle != 0)
                    StopVoiceConversation("session lost", ConversationEndReason.SessionLost);
                return;
            }

            // A connection that never produced a ready frame must not hold the
            // talk key hostage; otherwise the player cannot address anybody new.
            if (_owner.ConnectingExpired(DateTime.UtcNow))
            {
                StopVoiceConversation(_liveError ?? "the voice service did not answer",
                    ConversationEndReason.SessionLost);
                return;
            }

            if (!PlayerAlive(player))
            {
                StopVoiceConversation("player died", ConversationEndReason.PlayerDied);
                return;
            }

            int playerHandle = player.Character.Handle;
            if (_playerHandleLast != 0 && playerHandle != _playerHandleLast)
            {
                // A new player ped means a respawn or a scripted swap. The old
                // partner is not in this scene, and waiting for a distance check
                // that may never fire is how a conversation used to survive death.
                _playerHandleLast = playerHandle;
                StopVoiceConversation("player respawned", ConversationEndReason.PlayerDied);
                return;
            }
            _playerHandleLast = playerHandle;

            Ped ped = Entity.FromHandle(_liveNpcHandle) as Ped;
            bool pedValid = ped != null && ped.Exists() && !ped.IsDead;
            double distance = pedValid ? ped.Position.DistanceTo(player.Character.Position) : double.MaxValue;

            ConversationEndReason releaseReason;
            if (ConversationLifecyclePolicy.ShouldRelease(true, pedValid, distance,
                CurrentRange() + 15f, out releaseReason))
            {
                StopVoiceConversation(pedValid ? "you walked too far away" : "they are gone", releaseReason);
                return;
            }

            if (!_farewellPending) return;

            // The goodbye is over once the NPC stops speaking and the short
            // drain has elapsed; the hard deadline covers a farewell reply that
            // never arrives, so the player is never trapped in it.
            bool stillSpeaking = _livePlayer.IsPlaying ||
                DateTime.UtcNow.Ticks < Interlocked.Read(ref _npcAudioTicks);
            if ((_farewellHeardReply && !stillSpeaking && DateTime.UtcNow >= _farewellDeadlineUtc) ||
                DateTime.UtcNow >= _farewellHardDeadlineUtc)
            {
                StopVoiceConversation("you said goodbye", ConversationEndReason.PlayerSaidGoodbye);
            }
        }

        private static bool PlayerAlive(GTA.Player player)
        {
            try
            {
                if (player == null) return false;
                Ped ped = player.Character;
                if (ped == null || !ped.Exists()) return false;
                if (ped.IsDead) return false;
                return !Function.Call<bool>(Hash.IS_PED_DEAD_OR_DYING, ped, true);
            }
            catch
            {
                // A native failing here must not end a healthy conversation.
                return true;
            }
        }

        /// <summary>
        /// Frame-rate animation tick. Reads only values the audio threads wrote,
        /// so it never blocks on a microphone buffer or a network frame.
        /// </summary>
        private void UpdateAnimator()
        {
            ConversationAnimator animator = _animator;
            if (animator == null) return;

            DateTime nowUtc = DateTime.UtcNow;
            float npcLevel = Interlocked.CompareExchange(ref _npcLevel, 0f, 0f);
            bool npcAudio = nowUtc.Ticks < Interlocked.Read(ref _npcAudioTicks);
            bool playerTalking = nowUtc.Ticks - Interlocked.Read(ref _micVoiceTicks) <
                TimeSpan.TicksPerMillisecond * 350;
            animator.Tick(nowUtc, npcLevel, npcAudio, playerTalking);
        }

        private void StopVoiceConversation(string reason)
        {
            StopVoiceConversation(reason, MapEndReason(reason));
        }

        private void StopVoiceConversation(string reason, ConversationEndReason endReason)
        {
            // Address the conversation this caller means, and invalidate it on
            // this thread before doing anything else. A late microphone buffer
            // or Gemini frame then fails its ownership check immediately,
            // instead of reaching the partner while teardown waits for a tick.
            string conversationId = _owner.ConversationId;
            ConversationSession ending = _owner.BeginEnding(conversationId, endReason, DateTime.UtcNow);

            // Audio, pipe and network threads may discover that a session died.
            // They may not stop it themselves: releasing the hold and the face
            // calls natives. Hop onto the game thread instead.
            if (_gameThreadId != 0 && Thread.CurrentThread.ManagedThreadId != _gameThreadId)
            {
                _gameThread.Post(() => FinishTeardown(reason, endReason, conversationId));
                return;
            }

            FinishTeardown(reason, endReason, conversationId, ending);
        }

        /// <summary>
        /// The one teardown. Idempotent: a second request for a conversation that
        /// is already gone returns without touching whatever replaced it.
        /// </summary>
        private void FinishTeardown(string reason, ConversationEndReason endReason, string conversationId,
            ConversationSession ending = null)
        {
            ConversationSession current = _owner.Current;
            if (current != null && conversationId != null &&
                !string.Equals(current.ConversationId, conversationId, StringComparison.Ordinal))
            {
                PluginLog.Debug("Ignored a teardown for an earlier conversation.");
                return;
            }

            bool wasEngaged = _liveActive || _liveStarting || _owner.Phase != ConversationPhase.Idle;
            try
            {
                ConversationSession session = ending ?? _owner.Current;
                if (session != null) session.End(endReason, DateTime.UtcNow);
                _owner.Release();

                if (_engagement != null) _engagement.End(DateTime.UtcNow, endReason);
                _engagement = null;

                // Whatever ended the conversation, the NPC gets its own
                // behaviour back first: no lingering face task, no lingering
                // mission pin, no driver left braking in the road.
                ConversationHold hold = _hold;
                _hold = null;
                if (hold != null) hold.Release();

                // Mouth, expression and gestures are handed back too: nothing
                // from this conversation may outlive it.
                ConversationAnimator animator = _animator;
                _animator = null;
                if (animator != null) animator.Release();

                _liveActive = false;
                _liveReady = false;
                _liveStarting = false;
                lock (_talkSync) _talk.Reset();
                _pttHasSpeech = false;
                _liveNpcHandle = 0;
                _liveNpcId = null;
                _liveNpcName = null;
                _liveNpcLabel = null;
                _liveVoiceName = null;
                _farewellPending = false;
                _farewellHeardReply = false;
                _farewellDeadlineUtc = DateTime.MinValue;
                _farewellHardDeadlineUtc = DateTime.MinValue;
                _playerHandleLast = 0;

                Interlocked.Exchange(ref _npcAudioTicks, 0);
                Interlocked.Exchange(ref _npcLevel, 0f);
                Interlocked.Exchange(ref _micVoiceTicks, 0);

                _mic.Stop();
                _livePlayer.Flush();

                VoiceLink link = _voice;
                _voice = null;
                if (link != null)
                {
                    try { link.SendStop(); } catch { }
                    try { link.Dispose(); } catch { }
                }

                try { _liveCts?.Cancel(); } catch { }
                try { _liveCts?.Dispose(); } catch { }
                _liveCts = null;

                if (wasEngaged)
                {
                    if (!string.IsNullOrWhiteSpace(reason) && reason != "closed by player")
                        ShowMessage("Voice conversation ended: " + reason);
                    else if (reason == "closed by player")
                        ShowMessage("Stopped listening.");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Stopping the voice conversation failed: " + ex.Message);
            }
        }

        private void TriggerSocial()
        {
            int first = NearestBinding(_settings.ConversationRangeMeters);
            if (first == 0)
            {
                ShowMessage("No one nearby to start a conversation with.");
                return;
            }
            int second = NearestBinding(_settings.ConversationRangeMeters + 10f, first);
            if (second == 0)
            {
                ShowMessage("Need at least two people nearby for a social scene.");
                return;
            }

            var request = new MessageEnvelope
            {
                Type = MessageTypes.TriggerSocial,
                PayloadJson = JsonConvert.SerializeObject(new TriggerSocialRequest
                {
                    NpcHandle = first,
                    OtherHandle = second,
                    Force = true
                }, PipeClient.JsonSettings)
            };

            Task.Run(async () =>
            {
                try
                {
                    MessageEnvelope raw = await _pipe.SendAsync(request, new CancellationTokenSource(45000).Token).ConfigureAwait(false);
                    TriggerSocialResponse result = PipeClient.ParsePayload<TriggerSocialResponse>(raw);
                    if (result != null && !result.Ok) ShowMessage(result.Error ?? "Social request rejected.");
                }
                catch (Exception ex)
                {
                    ShowMessage("Social request failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Starts the attention lock for the addressed NPC. Timeouts are long on
        /// purpose: a person thinking for half a minute is still in the
        /// conversation, and the player may step around the car while talking.
        /// </summary>
        private void BeginEngagement(Ped ped, int handle, string npcId)
        {
            try
            {
                ConversationHold previous = _hold;
                _hold = null;
                if (previous != null) previous.Release();

                float range = _settings?.ConversationRangeMeters > 0
                    ? _settings.ConversationRangeMeters
                    : _config.ConversationRangeMeters;

                var settings = new EngagementSettings
                {
                    LeaveRadiusMeters = range + 15f,
                    InactivityTimeoutSeconds = _config.ConversationIdleTimeoutSeconds > 0
                        ? _config.ConversationIdleTimeoutSeconds
                        : 240,
                    FaceIntervalSeconds = 1.5,
                    HoldIntervalSeconds = 1.0
                };

                bool inVehicle = ped.IsInVehicle();
                var state = new EngagementState(settings);
                state.Begin(DateTime.UtcNow, handle, npcId, inVehicle,
                    inVehicle && ConversationHold.IsDriving(ped, ped.CurrentVehicle),
                    ped.CurrentVehicle?.Handle ?? 0);
                _engagement = state;

                _hold = new ConversationHold();
                _hold.Attach(ped);
                EngagementDecision first = state.Evaluate(DateTime.UtcNow, 0.0, true, true, false);
                if (first.Hold != EngagementHold.None) _hold.Apply(first.Hold);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Could not hold the conversation partner: " + ex.Message);
            }
        }

        /// <summary>
        /// Only genuine danger breaks a conversation: gunfire, a fight, being on
        /// fire. A wanted level or a nearby siren is not enough, or a lively
        /// street would end every conversation the moment a cruiser passed.
        /// </summary>
        private static bool IsEmergency(Ped player, Ped npc)
        {
            try
            {
                if (player == null || !player.Exists()) return false;
                if (GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_PED_SHOOTING, player)) return true;
                if (player.IsInCombat) return true;
                if (GTA.Native.Function.Call<bool>(GTA.Native.Hash.IS_ENTITY_ON_FIRE, player)) return true;
                if (npc != null && npc.Exists() && npc.IsInCombat) return true;
            }
            catch { }
            return false;
        }

        private static string ReasonText(ConversationEndReason reason)
        {
            switch (reason)
            {
                case ConversationEndReason.OutOfRange: return "you walked too far away";
                case ConversationEndReason.PlayerDied: return "player died";
                case ConversationEndReason.Unavailable: return "they are gone";
                case ConversationEndReason.NpcDied: return "they are gone";
                case ConversationEndReason.Emergency: return "something urgent interrupted";
                case ConversationEndReason.Inactivity: return "the conversation went quiet";
                case ConversationEndReason.PluginUnload: return "plugin unloading";
                case ConversationEndReason.PlayerEnteredVehicle: return "you got into a vehicle";
                case ConversationEndReason.PlayerSwitchedTarget: return "you turned to someone else";
                case ConversationEndReason.PlayerStopped: return "closed by player";
                default: return "session ended";
            }
        }

        private static ConversationEndReason MapEndReason(string reason)
        {
            switch ((reason ?? string.Empty).ToLowerInvariant())
            {
                case "closed by player": return ConversationEndReason.PlayerStopped;
                case "out of range": return ConversationEndReason.OutOfRange;
                case "player died": return ConversationEndReason.PlayerDied;
                case "they are gone": return ConversationEndReason.Unavailable;
                case "plugin unloading": return ConversationEndReason.PluginUnload;
                case "you turned to someone else": return ConversationEndReason.PlayerSwitchedTarget;
                case "you said goodbye": return ConversationEndReason.PlayerSaidGoodbye;
                default: return ConversationEndReason.SessionLost;
            }
        }

        // ------------------------------------------------------------- speech

        private void QueueSpeech(NpcSpeechEvent speech)
        {
            if (speech == null || string.IsNullOrWhiteSpace(speech.Text)) return;

            // Audio only: an NPC line with no rendered audio is dropped rather
            // than turned into a subtitle. Dialogue is heard, never read.
            if (string.IsNullOrWhiteSpace(speech.AudioPath) || !File.Exists(speech.AudioPath)) return;

            lock (_stateSync)
            {
                if (_speeches.Count > 24) return;

                // The line is played at its own level and then placed in the
                // stereo field by distance on every tick, so a pedestrian who
                // walks while talking keeps moving in the mix. Attenuating twice
                // (once here, once by distance) made distant speakers vanish.
                _speeches.Add(new PendingSpeech
                {
                    Text = speech.Text,
                    Speaker = speech.DisplayName,
                    AudioPath = speech.AudioPath,
                    Volume = 1f,
                    SpeakerHandle = speech.NpcHandle,
                    DueUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, speech.DelayMs))
                });
            }
        }

        private void PumpSpeeches()
        {
            // A live conversation owns the audio output.
            if (_liveActive) return;

            // Pre-rendered lines are placed in the same stereo field as live
            // ones, so NPC-to-NPC conversations are audible as coming from
            // where the speakers actually stand.
            int currentSpeaker = _audio.CurrentSpeakerHandle;
            if (currentSpeaker != 0)
            {
                float gain = 1f, pan = 0f;
                bool placed;
                SpatialFor(Entity.FromHandle(currentSpeaker) as Ped, out gain, out pan, out placed);
                if (placed) _audio.UpdateSpatial(gain, pan, DateTime.UtcNow);
            }

            PendingSpeech ready = null;
            lock (_stateSync)
            {
                if (_speeches.Count == 0) return;
                PendingSpeech first = _speeches[0];
                if (first.DueUtc <= DateTime.UtcNow && !_audio.IsPlaying)
                {
                    _speeches.RemoveAt(0);
                    ready = first;
                }
            }
            if (ready != null)
                _audio.Enqueue(ready.AudioPath, ready.Text, ready.Volume, ready.SpeakerHandle);
        }

        // ---------------------------------------------------------------- hud

        private void UpdateBindings()
        {
            lock (_stateSync)
            {
                if (_bindings.Count == 0) return;
                var stale = new List<int>();
                foreach (int handle in _bindings.Keys)
                {
                    Ped ped = Entity.FromHandle(handle) as Ped;
                    if (ped == null || !ped.Exists() || ped.IsDead) stale.Add(handle);
                }
                foreach (int handle in stale) _bindings.Remove(handle);
            }
        }

        private void DrawHud(GTA.Player player)
        {
            if (!_config.ShowHud) return;

            string status = !_pipe.IsConnected
                ? "AI: service starting"
                : (_lastSnapshot == null
                    ? "AI: connected, waiting for first snapshot"
                    : (_lastSnapshot.AiAvailable
                        ? "AI: online" + (_lastSnapshot.LiveAvailable ? " (live voice)" : "")
                        : "AI: offline" + (string.IsNullOrWhiteSpace(_lastSnapshot.AiStatus) ? "" : " - " + _lastSnapshot.AiStatus)));

            CachedText(ref _hudStatusText, ref _hudStatusKey, "status", status,
                new PointF(24f, 22f), 0.36f, Color.FromArgb(220, 235, 235, 235)).Draw();

            string live;
            if (_liveActive)
            {
                live = (_liveReady ? "LIVE " : "CONNECTING ") + (_liveNpcLabel ?? _liveNpcName) +
                    (_livePlayer.IsPlaying ? " is speaking" : " - listening") +
                    (_talk.Phase == TalkPhase.Holding
                        ? " [holding " + _config.VoiceHotkey + "]"
                        : (_talk.ShouldStream
                            ? " [listening]"
                            : " [hold " + _config.VoiceHotkey + " to talk]"));
            }
            else if (_liveStarting)
            {
                live = "CONNECTING to " + (_liveNpcLabel ?? _liveNpcName) + "...";
            }
            else if (_liveError != null && DateTime.UtcNow < _liveErrorUntilUtc)
            {
                live = "Voice: " + _liveError;
            }
            else
            {
                live = null;
            }

            if (!string.IsNullOrWhiteSpace(live))
            {
                CachedText(ref _hudRecordingText, ref _hudRecordingKey, "live", live,
                    new PointF(24f, 50f), 0.40f,
                    _liveError != null && DateTime.UtcNow < _liveErrorUntilUtc
                        ? Color.FromArgb(255, 255, 150, 120)
                        : (_livePlayer.IsPlaying ? Color.FromArgb(255, 140, 220, 150) : Color.FromArgb(255, 255, 210, 120))).Draw();
            }

            if (DateTime.UtcNow < _lastMessageUntilUtc && !string.IsNullOrEmpty(_lastMessage))
            {
                CachedText(ref _hudMessageText, ref _hudMessageKey, "msg", _lastMessage,
                    new PointF(24f, 78f), 0.34f, Color.FromArgb(230, 250, 230, 170)).Draw();
            }

            if (!_showPanel) return;

            var lines = new List<string>
            {
                "ANOTHER DAY IN GTA",
                _lastSnapshot == null ? "Waiting for the first snapshot..." : "Known NPCs: " + _lastSnapshot.KnownNpcCount + "  tracked: " + _lastSnapshot.Bindings.Count,
                _lastSnapshot == null ? "" : "Active conversations: " + _lastSnapshot.ActiveConversationCount,
                _lastSnapshot == null ? "" : "Service: " + _lastSnapshot.ServiceStatus,
                _pipe.Handshake == null ? "" : "Data: " + _pipe.Handshake.DataDirectory,
                _lastSnapshot == null ? "" : "Live voice: " + (_lastSnapshot.LiveAvailable
                    ? "ready (" + _lastSnapshot.LiveSessions + " sessions)"
                    : (_lastSnapshot.LiveReason ?? "off")),
                _liveActive ? "Talking to: " + (_liveNpcLabel ?? _liveNpcName) + " (voice " + (_liveVoiceName ?? "?") + ", mic " + (_mic.DeviceName ?? "default") + ")" : "",
                "Controls: " + ControlsHint()
            };

            string panelKey = string.Join("\n", lines) + "|" + Screen.Width + "x" + Screen.Height;
            if (_panelKey != panelKey)
            {
                _panelKey = panelKey;
                _panelTexts.Clear();
                float y = 110f;
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) { y += 22f; continue; }
                    _panelTexts.Add(new TextElement(line, new PointF(24f, y), 0.31f,
                        Color.FromArgb(220, 235, 235, 235), UiFont.ChaletLondon));
                    y += 22f;
                }
            }
            foreach (TextElement text in _panelTexts) text.Draw();
        }

        /// <summary>
        /// Rebuilds a cached text element only when its content or anchor changes.
        /// The HUD now draws every frame, so building elements per frame would be
        /// pure garbage-collector pressure inside the game process.
        /// </summary>
        private TextElement CachedText(ref TextElement element, ref string key, string id, string text,
            PointF position, float scale, Color color, Alignment alignment = Alignment.Left)
        {
            string wanted = id + "|" + text + "|" + position.X.ToString("0.0") + "," + position.Y.ToString("0.0");
            if (element == null || key != wanted)
            {
                key = wanted;
                element = new TextElement(text, position, scale, color, UiFont.ChaletLondon, alignment);
            }
            return element;
        }

        private void ShowMessage(string message)
        {
            _lastMessage = message;
            _lastMessageUntilUtc = DateTime.UtcNow.AddSeconds(6);
            PluginLog.Info(message);
        }

        // ------------------------------------------------------------ helpers

        private static Vec3 ToVec(Vector3 v) => new Vec3 { X = v.X, Y = v.Y, Z = v.Z };

        private static string SafeZone()
        {
            try { return World.GetZoneLocalizedName(Gta.Player.Character.Position); }
            catch { return "Los Santos"; }
        }

        private static string SafeWeather()
        {
            try { return World.Weather.ToString(); }
            catch { return "unknown"; }
        }

        private static string SafeVehicleName(Ped ped)
        {
            try
            {
                Vehicle vehicle = ped == null ? null : ped.CurrentVehicle;
                if (vehicle == null || !vehicle.Exists()) return null;
                string name = vehicle.DisplayName;
                return string.IsNullOrWhiteSpace(name) ? "car" : name;
            }
            catch { return null; }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try { StopVoiceConversation("plugin unloading"); } catch { }
            try { _owner.Release(); } catch { }
            try { _animator?.Release(); } catch { }
            try
            {
                _mic?.Dispose();
                _livePlayer?.Dispose();
                _audio?.Dispose();
                _pipe?.Dispose();
            }
            catch { }
            PluginLog.Info("Another Day in GTA plugin unloaded.");
        }
    }
}
