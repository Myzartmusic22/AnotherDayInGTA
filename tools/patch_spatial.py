import io

p = 'src/AnotherDay.Plugin/Main.cs'
s = io.open(p, encoding='utf-8').read()
done = []


def rep(old, new, tag):
    global s
    assert old in s, 'ANCHOR MISSING: ' + tag
    s = s.replace(old, new, 1)
    done.append(tag)


rep("""            public string AudioPath;
            public float Volume;
            public DateTime DueUtc;""",
"""            public string AudioPath;
            public float Volume;
            public int SpeakerHandle;
            public DateTime DueUtc;""", 'pending speech handle')

rep("""                if (_speeches.Count > 24) return;
                float volume = 1f;
                Ped ped = Entity.FromHandle(speech.NpcHandle) as Ped;
                if (ped != null && ped.Exists())
                {
                    float distance = ped.Position.DistanceTo(Gta.Player.Character.Position);
                    float range = Math.Max(6f, _settings.ConversationRangeMeters);
                    volume = Math.Max(0.15f, 1f - (distance / range));
                }
                _speeches.Add(new PendingSpeech
                {
                    Text = speech.Text,
                    Speaker = speech.DisplayName,
                    AudioPath = speech.AudioPath,
                    Volume = volume,
                    DueUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, speech.DelayMs))
                });""",
"""                if (_speeches.Count > 24) return;

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
                });""", 'queue speech')

rep("""            float range = CurrentRange();
            float attenuation = Math.Max(0.25f, 1f - ((float)distance / Math.Max(2.5f, range * 2f)));
            _livePlayer.SetVolume(_config.Volume * Math.Min(1f, _settings?.Volume ?? 1f) * attenuation);""",
"""            // The voice comes from the pedestrian, not from the player's head:
            // gain follows distance and pan follows the side they stand on, both
            // smoothed inside the player so walking past them slides.
            float gain = 1f, pan = 0f;
            bool placed;
            SpatialFor(ped, out gain, out pan, out placed);
            _livePlayer.SetVolume(_config.Volume * Math.Min(1f, _settings?.Volume ?? 1f));
            _livePlayer.SetSpatial(placed ? gain : 1f, pan, DateTime.UtcNow);""", 'live spatial')

rep("""            // A live conversation owns the audio output.
            if (_liveActive) return;

            PendingSpeech ready = null;""",
"""            // A live conversation owns the audio output.
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

            PendingSpeech ready = null;""", 'pump spatial')

rep("""            if (ready != null) _audio.Enqueue(ready.AudioPath, ready.Text, ready.Volume);""",
"""            if (ready != null)
                _audio.Enqueue(ready.AudioPath, ready.Text, ready.Volume, ready.SpeakerHandle);""", 'enqueue handle')


# The listener frame helper, inserted before the lifecycle clock.
anchor = """        /// <summary>
        /// The one lifecycle clock."""
helper = """        /// <summary>
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
                    Ped player = Gta.Game.Player == null ? null : Gta.Game.Player.Character;
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

""" + anchor
rep(anchor, helper, 'spatial helper')

io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('patched:', ', '.join(done))
