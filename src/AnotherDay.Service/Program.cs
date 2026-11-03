using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Ipc;
using AnotherDay.Service.Security;
using AnotherDay.Shared;

namespace AnotherDay.Service
{
    internal static class Program
    {
        private const string ServiceVersion = "0.1.0";

        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string dataDirectory = null;
            string pipeName = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data":
                        if (i + 1 < args.Length) dataDirectory = args[++i];
                        break;
                    case "--pipe":
                        if (i + 1 < args.Length) pipeName = args[++i];
                        break;
                    case "--set-key":
                        return SetKey(new AppPaths(dataDirectory));
                    case "--set-key-file":
                        if (i + 1 >= args.Length) { Console.Error.WriteLine("--set-key-file needs a path."); return 1; }
                        return SetKeyFromFile(new AppPaths(dataDirectory), args[++i]);
                    case "--check-key":
                        return CheckKey(new AppPaths(dataDirectory));
                    case "--live-test":
                    {
                        string wav = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                        string say = null;
                        if (i + 2 < args.Length && args[i + 1] == "--say") { say = args[i + 2]; i += 2; }
                        return await LiveTestAsync(new AppPaths(dataDirectory), wav, say);
                    }
                    case "--print-data-dir":
                        Console.WriteLine(new AppPaths(dataDirectory).Root);
                        return 0;
                    case "--version":
                        Console.WriteLine(ServiceVersion);
                        return 0;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                }
            }

            var paths = new AppPaths(dataDirectory);
            paths.EnsureCreated();
            Log.Initialize(paths.LogDirectory);
            Log.Info("Another Day in GTA service " + ServiceVersion + " starting.");

            using var instance = new Mutex(true, "Local\\AnotherDayInGTA.Service.Singleton", out bool createdNew);
            if (!createdNew)
            {
                Log.Warn("Another service instance is already running; this one will exit.");
                Console.Error.WriteLine("Another Day in GTA service is already running.");
                return 2;
            }

            AppConfig config = AppConfig.Load(paths.ConfigPath);
            if (string.IsNullOrWhiteSpace(config.SourcePath)) config.SourcePath = paths.ConfigPath;
            if (!string.IsNullOrWhiteSpace(pipeName)) config.Service.PipeName = pipeName;
            if (!File.Exists(paths.ConfigPath)) config.Save();

            string token = LoadOrCreateToken(paths.TokenPath);
            var state = new ServiceState(paths, config, token);

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Log.Info("Shutdown requested.");
                try { shutdown.Cancel(); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { shutdown.Cancel(); } catch { } };

            try
            {
                await state.InitializeAsync().ConfigureAwait(false);
                Log.Info("Data directory: " + paths.Root);
                Log.Info("Database: " + (state.Database.IsAvailable ? "ready" : "unavailable, running in memory"));
                Log.Info("Gemini: " + (state.Conversation.AiConfigured ? "configured" : "not configured (offline behavior active)"));

                if (state.Conversation.AiConfigured)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                            cts.CancelAfter(TimeSpan.FromSeconds(20));
                            bool ok = await state.GeminiClient.CheckReachableAsync(cts.Token).ConfigureAwait(false);
                            Log.Info("Gemini health check: " + (ok ? "reachable" : "not reachable"));
                        }
                        catch (Exception ex) { Log.Warn("Gemini health check failed: " + ex.Message); }
                    });
                }

                Task socialLoop = RunSocialLoopAsync(state, shutdown.Token);
                var liveVoice = new LiveVoiceServer(Protocol.VoicePipeName(config.Service.PipeName), state, shutdown.Token);
                Task voiceLoop = liveVoice.RunAsync();
                var server = new PipeServer(config.Service.PipeName, state, shutdown.Token);
                await server.RunAsync().ConfigureAwait(false);
                try { await voiceLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await socialLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Fatal service error: " + ex.Message, ex);
                Console.Error.WriteLine("Another Day in GTA service failed: " + ex.Message);
                return 1;
            }
            finally
            {
                try { if (state.Database != null) await state.Database.DisposeAsync().ConfigureAwait(false); } catch { }
                Log.Info("Service stopped.");
                Log.Flush();
            }
            return 0;
        }

        private static async Task RunSocialLoopAsync(ServiceState state, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                    await state.Social.TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn("Social loop error: " + ex.Message);
                }
            }
        }

        private static string LoadOrCreateToken(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (existing.Length >= 32) return existing;
                }
            }
            catch { }

            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            string token = Convert.ToBase64String(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, token, new UTF8Encoding(false));
            File.Move(temp, path, true);
            try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
            return token;
        }

        private static int SetKey(AppPaths paths)
        {
            paths.EnsureCreated();
            string key;
            if (Console.IsInputRedirected)
            {
                key = Console.In.ReadLine();
                Console.Error.WriteLine("Key read from redirected input.");
            }
            else
            {
                Console.Error.Write("Paste your Gemini API key (input hidden): ");
                key = ReadHiddenLine();
                Console.Error.WriteLine();
            }
            return StoreKey(paths, key);
        }

        /// <summary>
        /// Answers the one question the logs cannot: is the stored key accepted
        /// by Google? Prints only the verdict and the API's error text; the key
        /// itself never leaves memory.
        /// </summary>
        private static int CheckKey(AppPaths paths)
        {
            string key = new SecretStore(paths).GetApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine("No Gemini API key is stored.");
                return 1;
            }

            Console.WriteLine("Key fingerprint: " + SecretStore.Fingerprint(key) +
                              " (prefix " + key.Substring(0, Math.Min(3, key.Length)) + ", value never printed)");
            if (SecretStore.LooksMalformed(key, out string malformed))
                Console.WriteLine("  warning: malformed key: " + malformed);

            AppConfig config = AppConfig.Load(paths.ConfigPath);
            string url = config.Gemini.ApiBaseUrl.TrimEnd('/') + "/v1beta/models?pageSize=1";

            // An API key is accepted in the x-goog-api-key header and in the key
            // query parameter; the Live WebSocket can only use the query form.
            // Testing both separates "bad key value" from "this endpoint or auth
            // style refuses the key", which is the difference between re-copying
            // a key and rewriting the client.
            var attempts = new (string Label, string Url, bool Header)[]
            {
                ("x-goog-api-key header", url, true),
                ("?key= query parameter", url + "&key=" + Uri.EscapeDataString(key), false)
            };

            bool accepted = false;
            foreach (var attempt in attempts)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    using var request = new HttpRequestMessage(HttpMethod.Get, attempt.Url);
                    if (attempt.Header) request.Headers.Add("x-goog-api-key", key);
                    using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("  " + attempt.Label + ": accepted (HTTP " + (int)response.StatusCode + ")");
                        accepted = true;
                        continue;
                    }

                    body = System.Text.RegularExpressions.Regex.Replace(body, "\\s+", " ").Trim().Replace(key, "<key>");
                    if (body.Length > 300) body = body.Substring(0, 300) + "...";
                    Console.WriteLine("  " + attempt.Label + ": HTTP " + (int)response.StatusCode);
                    if (body.Length > 0) Console.WriteLine("    " + body);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  " + attempt.Label + ": " + ex.Message);
                }
            }

            if (accepted)
            {
                Console.WriteLine("Gemini API key is valid.");
                return 0;
            }

            Console.WriteLine("Gemini refused this key for every auth style. The value stored here is not a key Google still knows: re-run --set-key and paste the same key again, single copy, no quotes.");
            return 1;
        }

        /// <summary>
        /// Opens a real Gemini Live session and reports what the server says. With
        /// a 16 kHz mono WAV path it also streams that audio as the player turn, so
        /// a spoken reply can be checked without launching the game.
        /// </summary>
        private static async Task<int> LiveTestAsync(AppPaths paths, string wavPath, string chatText = null)
        {
            AppConfig config = AppConfig.Load(paths.ConfigPath);
            string key = new SecretStore(paths).GetApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine("No Gemini API key is stored. Run --set-key first.");
                return 1;
            }

            var received = new List<byte[]>();
            var client = new Live.GeminiLiveClient(config, () => new Live.WebSocketLiveTransport(), key,
                config.Live.Model, "Kore", "You are a person on a Los Santos street corner, answering briefly.");
            client.AudioReceived += chunk => { lock (received) received.Add(chunk); };
            client.InputTranscript += text => Console.WriteLine("  heard: " + text);
            client.OutputTranscript += text => Console.WriteLine("  said: " + text);
            client.Warning += message => Console.WriteLine("  warning: " + message);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                Console.WriteLine("Live setup accepted: " + client.Model + " (voice " + client.VoiceName + ")");

                if (!string.IsNullOrWhiteSpace(chatText))
                {
                    Console.WriteLine("Sending a text turn: " + chatText);
                    await client.SendTextAsync(chatText, cts.Token).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(wavPath)
                    && File.Exists(wavPath))
                {
                    byte[] pcm = File.ReadAllBytes(wavPath);
                    Console.WriteLine("Streaming " + (pcm.Length / 32) + " ms of audio from " + Path.GetFileName(wavPath));
                    for (int offset = 0; offset < pcm.Length; offset += 3200)
                    {
                        int count = Math.Min(3200, pcm.Length - offset);
                        var chunk = new byte[count];
                        Buffer.BlockCopy(pcm, offset, chunk, 0, count);
                        await client.SendAudioAsync(chunk, cts.Token).ConfigureAwait(false);
                        await Task.Delay(100, cts.Token).ConfigureAwait(false);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(wavPath) && string.IsNullOrWhiteSpace(chatText) ? 2 : 20), cts.Token).ConfigureAwait(false);
                int bytes;
                lock (received) bytes = received.Sum(r => r.Length);
                Console.WriteLine("Audio received: " + bytes + " bytes" + (bytes > 0 ? " (native audio is working)" : ""));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Live test failed: " + ex.Message);
                return 1;
            }
            finally
            {
                client.Dispose();
            }
        }

        private static int SetKeyFromFile(AppPaths paths, string file)
        {
            try
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine("Key file not found.");
                    return 1;
                }
                return StoreKey(paths, File.ReadAllText(file).Trim());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read key file: " + ex.Message);
                return 1;
            }
        }

        private static int StoreKey(AppPaths paths, string key)
        {
            try
            {
                new SecretStore(paths).SetApiKey(key);
                Console.WriteLine("Gemini API key stored with Windows DPAPI protection.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not store the API key: " + ex.Message);
                return 1;
            }
        }

        private static string ReadHiddenLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
            return sb.ToString().Trim();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Another Day in GTA - local AI service " + ServiceVersion);
            Console.WriteLine();
            Console.WriteLine("  AnotherDay.Service.exe                 Run the service");
            Console.WriteLine("  AnotherDay.Service.exe --set-key       Store a Gemini API key (hidden input)");
            Console.WriteLine("  AnotherDay.Service.exe --set-key-file <path>");
            Console.WriteLine("  AnotherDay.Service.exe --check-key      Verify the stored key with Google");
            Console.WriteLine("  AnotherDay.Service.exe --live-test [wav] [--say text]  Open a Gemini Live session and report audio back");
            Console.WriteLine("  AnotherDay.Service.exe --print-data-dir");
            Console.WriteLine("  --data <directory>                     Override the persistent data directory");
            Console.WriteLine("  --pipe <name>                          Override the IPC pipe name");
        }
    }
}
