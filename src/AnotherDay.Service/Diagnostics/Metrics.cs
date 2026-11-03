using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AnotherDay.Service.Diagnostics
{
    public sealed class Metrics
    {
        private long _apiRequestsToday;
        private long _estimatedTokensToday;
        private long _failedRequests;
        private long _ttsRequests;
        private long _fallbackTtsRequests;
        private long _transcriptions;
        private int _activeConversations;
        private string _lastError;
        private DateTime _lastErrorUtc;
        private readonly ConcurrentDictionary<string, long> _counters = new ConcurrentDictionary<string, long>();
        private DateTime _day = DateTime.UtcNow.Date;
        private readonly object _dayLock = new object();

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public bool LastAiCallSucceeded { get; private set; }
        public DateTime? LastAiCallUtc { get; private set; }
        public long ApiRequestsToday => Interlocked.Read(ref _apiRequestsToday);
        public long EstimatedTokensToday => Interlocked.Read(ref _estimatedTokensToday);
        public long FailedRequests => Interlocked.Read(ref _failedRequests);
        public long TtsRequests => Interlocked.Read(ref _ttsRequests);
        public long FallbackTtsRequests => Interlocked.Read(ref _fallbackTtsRequests);
        public long Transcriptions => Interlocked.Read(ref _transcriptions);
        public int ActiveConversations => Volatile.Read(ref _activeConversations);
        public string LastError => _lastError;
        public DateTime? LastErrorUtc => _lastErrorUtc == default ? null : _lastErrorUtc;
        public double UptimeSeconds => (DateTime.UtcNow - StartedUtc).TotalSeconds;

        public void Increment(string name)
        {
            _counters.AddOrUpdate(name, 1, (_, v) => v + 1);
        }

        public long Get(string name) => _counters.TryGetValue(name, out long v) ? v : 0;

        public void RecordApiCall(long estimatedTokens, bool success)
        {
            RollDay();
            Interlocked.Increment(ref _apiRequestsToday);
            Interlocked.Add(ref _estimatedTokensToday, Math.Max(0, estimatedTokens));
            LastAiCallSucceeded = success;
            LastAiCallUtc = DateTime.UtcNow;
            if (!success) Interlocked.Increment(ref _failedRequests);
        }

        public void RecordTts(bool fallback)
        {
            Interlocked.Increment(ref _ttsRequests);
            if (fallback) Interlocked.Increment(ref _fallbackTtsRequests);
        }

        public void RecordTranscription() => Interlocked.Increment(ref _transcriptions);

        public void SetActiveConversations(int count) => Volatile.Write(ref _activeConversations, count);

        public void RecordError(string message)
        {
            _lastError = message;
            _lastErrorUtc = DateTime.UtcNow;
        }

        private void RollDay()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (today == _day) return;
            lock (_dayLock)
            {
                if (today == _day) return;
                _day = today;
                Interlocked.Exchange(ref _apiRequestsToday, 0);
                Interlocked.Exchange(ref _estimatedTokensToday, 0);
                Interlocked.Exchange(ref _failedRequests, 0);
            }
        }
    }
}
