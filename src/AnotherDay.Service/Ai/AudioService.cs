using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Data;
using AnotherDay.Service.Diagnostics;
using AnotherDay.Service.Domain;

namespace AnotherDay.Service.Ai
{
    public sealed class AudioResult
    {
        public string Path { get; set; }
        public string VoiceName { get; set; }
        public bool Fallback { get; set; }
        public long Bytes { get; set; }
    }

    public sealed class AudioService
    {
        private readonly AppConfig _config;
        private readonly AppPaths _paths;
        private readonly Database _db;
        private readonly IVoiceProvider _voiceProvider;
        private readonly Metrics _metrics;
        private readonly SemaphoreSlim _gate;

        public AudioService(AppConfig config, AppPaths paths, Database db, IVoiceProvider voiceProvider, Metrics metrics)
        {
            _config = config;
            _paths = paths;
            _db = db;
            _voiceProvider = voiceProvider;
            _metrics = metrics;
            _gate = new SemaphoreSlim(Math.Max(1, config.Service.MaxConcurrentAiRequests), Math.Max(1, config.Service.MaxConcurrentAiRequests));
        }

        public async Task<AudioResult> SynthesizeAsync(NpcProfile profile, EmotionState emotion, string text, CancellationToken ct)
        {
            if (!_config.Voice.Enabled || !_config.UserSettings.VoicesEnabled || string.IsNullOrWhiteSpace(text))
                return null;

            VoiceAssignment voice = profile.Voice ?? new VoiceAssignment
            {
                Provider = "gemini",
                VoiceId = profile.IsMale ? "Alnilam" : "Kore",
                VoiceName = profile.IsMale ? "Alnilam" : "Kore",
                Style = VoiceEngine.BuildStyle(profile, emotion)
            };
            voice.Style = VoiceEngine.BuildStyle(profile, emotion);

            string cacheKey = CacheKey(profile.NpcId, voice.VoiceId, voice.Style, text);
            string cachePath = Path.Combine(_paths.AudioCacheDirectory, cacheKey.Substring(0, 2), cacheKey + ".wav");
            if (File.Exists(cachePath))
            {
                var info = new FileInfo(cachePath);
                if (info.Length > 44)
                {
                    await _db.RecordAudioCacheAsync(cacheKey, cachePath, info.Length).ConfigureAwait(false);
                    _metrics.Increment("tts.cache_hit");
                    return new AudioResult { Path = cachePath, VoiceName = voice.VoiceName, Fallback = voice.IsFallback, Bytes = info.Length };
                }
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(cachePath)) return new AudioResult { Path = cachePath, VoiceName = voice.VoiceName, Fallback = voice.IsFallback };

                byte[] audio = null;
                bool fallback = false;
                string usedVoice = voice.VoiceName;

                if (_voiceProvider.IsConfigured && _config.UserSettings.AiEnabled && !voice.IsFallback)
                {
                    try
                    {
                        audio = await _voiceProvider.SynthesizeAsync(text, voice.VoiceId, voice.Style, ct).ConfigureAwait(false);
                        _metrics.RecordTts(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn("Gemini TTS failed for " + profile.DisplayName + ": " + ex.Message);
                        _metrics.RecordError("TTS failed: " + ex.Message);
                    }
                }

                // No operating-system speech fallback exists any more. If Gemini
                // TTS cannot speak, the line is shown as a subtitle instead of
                // being read aloud by a Windows voice.
                if (audio == null || audio.Length <= 44) return null;

                string dir = Path.GetDirectoryName(cachePath);
                Directory.CreateDirectory(dir);
                string temp = cachePath + ".tmp";
                await File.WriteAllBytesAsync(temp, audio, ct).ConfigureAwait(false);
                File.Move(temp, cachePath, true);

                await _db.RecordAudioCacheAsync(cacheKey, cachePath, audio.Length).ConfigureAwait(false);
                await PruneCacheAsync().ConfigureAwait(false);

                return new AudioResult { Path = cachePath, VoiceName = usedVoice, Fallback = fallback, Bytes = audio.Length };
            }
            finally { _gate.Release(); }
        }

        private async Task PruneCacheAsync()
        {
            try
            {
                var entries = await _db.GetAudioCacheEntriesAsync().ConfigureAwait(false);
                long total = entries.Sum(e => e.Bytes);
                long limit = _config.Voice.MaxCacheMegabytes * 1024L * 1024L;
                if (total <= limit) return;

                foreach (var entry in entries.OrderBy(e => e.UsedUtc))
                {
                    if (total <= limit * 0.8) break;
                    try { if (File.Exists(entry.Path)) File.Delete(entry.Path); } catch { }
                    await _db.DeleteAudioCacheEntryAsync(entry.Key).ConfigureAwait(false);
                    total -= entry.Bytes;
                }
                Log.Info("Pruned audio cache to " + (total / 1024 / 1024) + " MB");
            }
            catch (Exception ex)
            {
                Log.Debug("Audio cache prune failed: " + ex.Message);
            }
        }

        private static string CacheKey(string npcId, string voiceId, string style, string text)
        {
            string raw = npcId + "|" + voiceId + "|" + style + "|" + text;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
