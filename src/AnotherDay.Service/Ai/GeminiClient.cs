using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Diagnostics;

namespace AnotherDay.Service.Ai
{
    public sealed class GeminiApiException : Exception
    {
        public int StatusCode { get; }
        public GeminiApiException(string message, int statusCode = 0, Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Thin REST client for the Gemini Interactions API, TTS, and Files API.
    /// Uses the API key header, never the URL, so keys cannot leak into logs.
    /// </summary>
    public sealed class GeminiClient
    {
        private readonly AppConfig _config;
        private readonly Metrics _metrics;
        private readonly Security.SecretStore _secrets;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _concurrency;
        private readonly SemaphoreSlim _rateGate = new SemaphoreSlim(1, 1);
        private readonly Queue<DateTime> _requestTimes = new Queue<DateTime>();
        private int _authRejected;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private const string ApiVersion = "v1beta";

        public GeminiClient(AppConfig config, Metrics metrics, Security.SecretStore secrets)
        {
            _config = config;
            _metrics = metrics;
            _secrets = secrets;
            _concurrency = new SemaphoreSlim(config.Service.MaxConcurrentAiRequests, config.Service.MaxConcurrentAiRequests);
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AnotherDayInGTA/0.1");
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_secrets.GetApiKey());

        /// <summary>
        /// The key for building the Live WebSocket URL. Kept inside the service:
        /// it never enters a log, the plugin, or a config file.
        /// </summary>
        internal string ApiKeyForLive => _secrets.GetApiKey();

        /// <summary>
        /// True once Google has rejected the stored key (HTTP 401/403). A key
        /// being present is not the same as a key working, so the HUD and voice
        /// prompts must not claim the AI is online while every call is refused.
        /// Cleared as soon as a call succeeds.
        /// </summary>
        public bool AuthRejected => Volatile.Read(ref _authRejected) != 0;

        private void NoteAuthRejected(int status)
        {
            if (Interlocked.Exchange(ref _authRejected, status) == 0)
                Log.Error("Gemini rejected the stored API key (HTTP " + status +
                    "). Dialogue and voices need a valid key: run AnotherDay.Service.exe --set-key.");
        }

        private void NoteAuthAccepted() => Interlocked.Exchange(ref _authRejected, 0);
        public string BaseUrl => _config.Gemini.ApiBaseUrl.TrimEnd('/');
        public string TextModel => _config.Gemini.TextModel;
        public string TtsModel => _config.Gemini.TtsModel;
        public string TranscribeModel => _config.Gemini.TranscribeModel;

        public async Task<JsonDocument> CreateInteractionAsync(object body, string purpose, CancellationToken ct)
        {
            string key = _secrets.GetApiKey();
            if (string.IsNullOrWhiteSpace(key))
                throw new AiUnavailableException("Gemini API key is not configured.");

            string url = BaseUrl + "/" + ApiVersion + "/interactions";
            string json = JsonSerializer.Serialize(body, _json);
            return await SendJsonAsync(HttpMethod.Post, url, json, key, purpose, ct).ConfigureAwait(false);
        }

        public async Task<JsonDocument> ListVoicesAsync(CancellationToken ct)
        {
            string key = _secrets.GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) throw new AiUnavailableException("Gemini API key is not configured.");
            string url = BaseUrl + "/" + ApiVersion + "/voices?page_size=100";
            return await SendJsonAsync(HttpMethod.Get, url, null, key, "voices.list", ct).ConfigureAwait(false);
        }

        public async Task<bool> CheckReachableAsync(CancellationToken ct)
        {
            string key = _secrets.GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                string url = BaseUrl + "/" + ApiVersion + "/models/" + Uri.EscapeDataString(_config.Gemini.TextModel);
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, url, null, key, "health", ct, retries: 0).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Gemini health check failed: " + ex.Message);
                return false;
            }
        }

        public async Task<string> UploadFileAsync(string path, string mimeType, CancellationToken ct)
        {
            string key = _secrets.GetApiKey();
            if (string.IsNullOrWhiteSpace(key)) throw new AiUnavailableException("Gemini API key is not configured.");
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes.Length == 0) throw new InvalidOperationException("Recording is empty.");

            string startUrl = BaseUrl + "/upload/" + ApiVersion + "/files";
            string metadata = JsonSerializer.Serialize(new { file = new { display_name = "anotherday-input-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") } });

            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WaitForRateAsync(ct).ConfigureAwait(false);
                using var startRequest = new HttpRequestMessage(HttpMethod.Post, startUrl);
                startRequest.Headers.Add("x-goog-api-key", key);
                startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                startRequest.Headers.Add("X-Goog-Upload-Command", "start");
                startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
                startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
                startRequest.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

                using HttpResponseMessage startResponse = await _http.SendAsync(startRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!startResponse.IsSuccessStatusCode)
                {
                    if ((int)startResponse.StatusCode == 401 || (int)startResponse.StatusCode == 403)
                        NoteAuthRejected((int)startResponse.StatusCode);
                    throw new GeminiApiException("Could not start file upload: " + await SafeErrorAsync(startResponse, ct).ConfigureAwait(false), (int)startResponse.StatusCode);
                }

                if (!startResponse.Headers.TryGetValues("x-goog-upload-url", out IEnumerable<string> values))
                    throw new GeminiApiException("Gemini did not return an upload URL.");
                string uploadUrl = values.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(uploadUrl)) throw new GeminiApiException("Gemini returned an empty upload URL.");

                using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
                uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                uploadRequest.Content = new ByteArrayContent(bytes);
                uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                uploadRequest.Content.Headers.ContentLength = bytes.Length;

                using HttpResponseMessage uploadResponse = await _http.SendAsync(uploadRequest, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                string payload = await uploadResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!uploadResponse.IsSuccessStatusCode)
                    throw new GeminiApiException("File upload failed: " + Trim(payload, 300), (int)uploadResponse.StatusCode);

                using JsonDocument doc = JsonDocument.Parse(payload);
                JsonElement file = doc.RootElement.TryGetProperty("file", out JsonElement f) ? f : doc.RootElement;
                if (!file.TryGetProperty("uri", out JsonElement uri)) throw new GeminiApiException("Upload response had no file URI.");
                _metrics.Increment("files.uploaded");
                return uri.GetString();
            }
            finally { _concurrency.Release(); }
        }

        public async Task DeleteFileAsync(string fileName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            try
            {
                string key = _secrets.GetApiKey();
                if (string.IsNullOrWhiteSpace(key)) return;
                string url = BaseUrl + "/" + ApiVersion + "/" + fileName.TrimStart('/');
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Add("x-goog-api-key", key);
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug("Uploaded file cleanup failed: " + ex.Message);
            }
        }

        private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string json, string key, string purpose,
            CancellationToken ct, int retries = 2)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await _concurrency.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await WaitForRateAsync(ct).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(_config.Service.AiTimeoutSeconds));
                    using var request = new HttpRequestMessage(method, url);
                    request.Headers.Add("x-goog-api-key", key);
                    if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                    string payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        if (status == 401 || status == 403) NoteAuthRejected(status);
                        _metrics.RecordApiCall(EstimateTokens(json, payload), false);
                        string message = SafeApiMessage(payload, status);
                        last = new GeminiApiException(message, status);
                        if (status == 429 || status == 408 || status >= 500)
                        {
                            if (attempt < retries)
                            {
                                int delay = response.Headers.RetryAfter?.Delta.HasValue == true
                                    ? (int)Math.Min(30, response.Headers.RetryAfter.Delta.Value.TotalSeconds)
                                    : (int)Math.Pow(2, attempt + 1);
                                Log.Warn("Gemini " + purpose + " HTTP " + status + ", retrying in " + delay + "s");
                                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                                continue;
                            }
                        }
                        _metrics.RecordError("Gemini " + purpose + " failed: " + message);
                        throw last;
                    }

                    NoteAuthAccepted();
                    _metrics.RecordApiCall(EstimateTokens(json, payload), true);
                    _metrics.Increment("gemini." + purpose);
                    await Task.Delay(0).ConfigureAwait(false);
                    return JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
                }
                catch (GeminiApiException) { throw; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < retries)
                {
                    last = new AiUnavailableException("Gemini request timed out.");
                    _metrics.RecordApiCall(EstimateTokens(json, null), false);
                    Log.Warn("Gemini " + purpose + " timed out, retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < retries)
                {
                    last = new AiUnavailableException("Gemini network error: " + ex.Message, ex);
                    _metrics.RecordApiCall(EstimateTokens(json, null), false);
                    Log.Warn("Gemini " + purpose + " network error, retrying: " + ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (attempt >= retries)
                {
                    throw new AiUnavailableException("Gemini request failed: " + ex.Message, ex);
                }
                finally
                {
                    _concurrency.Release();
                }
            }
            throw last ?? new AiUnavailableException("Gemini request failed.");
        }

        private async Task WaitForRateAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan wait;
                await _rateGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    DateTime now = DateTime.UtcNow;
                    while (_requestTimes.Count > 0 && (now - _requestTimes.Peek()).TotalSeconds > 60)
                        _requestTimes.Dequeue();

                    if (_metrics.ApiRequestsToday >= _config.Service.DailyRequestBudget)
                        throw new AiUnavailableException("Daily Gemini request budget reached. The city will use local behavior until tomorrow.");

                    int limit = Math.Max(1, _config.Service.RequestsPerMinute);
                    if (_requestTimes.Count < limit)
                    {
                        _requestTimes.Enqueue(now);
                        return;
                    }
                    wait = TimeSpan.FromSeconds(60 - (now - _requestTimes.Peek()).TotalSeconds + 0.25);
                }
                finally { _rateGate.Release(); }

                Log.Warn("Gemini rate limit reached; waiting " + wait.TotalSeconds.ToString("0.0") + "s");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        private static string SafeApiMessage(string payload, int status)
        {
            if (string.IsNullOrWhiteSpace(payload)) return "HTTP " + status;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
                    string code = error.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(message))
                        return "HTTP " + status + (string.IsNullOrWhiteSpace(code) ? "" : " " + code) + ": " + Trim(message, 400);
                }
            }
            catch { }
            return "HTTP " + status + ": " + Trim(payload, 300);
        }

        private static async Task<string> SafeErrorAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return SafeApiMessage(text, (int)response.StatusCode);
            }
            catch { return "HTTP " + (int)response.StatusCode; }
        }

        private static long EstimateTokens(string request, string response)
        {
            long chars = (request?.Length ?? 0) + (response?.Length ?? 0);
            return Math.Max(1, chars / 4);
        }

        private static string Trim(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "...";
    }
}
