using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AnotherDay.Service
{
    public enum LogLevel { Debug, Info, Warn, Error }

    /// <summary>
    /// Small rolling logger. Avoids external logging dependencies so the
    /// shipped service has a minimal footprint. Never writes API keys.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static string _directory;
        private static int _writerRunning;
        private static string _currentDate;

        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        public static void Initialize(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Error("Unhandled exception: " + e.ExceptionObject);
                Flush();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Warn(string message, Exception ex = null) => Write(LogLevel.Warn, message, ex);
        public static void Error(string message, Exception ex = null) => Write(LogLevel.Error, message, ex);

        private static void Write(LogLevel level, string message, Exception ex)
        {
            if (level < MinimumLevel) return;
            string line = string.Format(
                "{{\"ts\":\"{0:o}\",\"level\":\"{1}\",\"msg\":{2}{3}}}",
                DateTime.UtcNow,
                level.ToString().ToLowerInvariant(),
                JsonEscape(Redact(message)),
                ex == null ? "" : ",\"exception\":" + JsonEscape(Redact(ex.ToString())));

            Pending.Enqueue(line);
            Console.WriteLine("[{0:HH:mm:ss}] {1}: {2}", DateTime.Now, level, message);
            EnsureWriter();
        }

        private static void EnsureWriter()
        {
            if (Interlocked.CompareExchange(ref _writerRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Drain(); }
                finally { Interlocked.Exchange(ref _writerRunning, 0); }
            });
        }

        private static void Drain()
        {
            if (string.IsNullOrEmpty(_directory)) return;
            string date = DateTime.UtcNow.ToString("yyyyMMdd");
            string path = Path.Combine(_directory, "service-" + date + ".log");
            try
            {
                lock (Sync)
                {
                    if (_currentDate != date)
                    {
                        _currentDate = date;
                        PruneOldLogs();
                    }
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    while (Pending.TryDequeue(out string line))
                        writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[log] " + ex.Message);
            }
        }

        public static void Flush()
        {
            try { Drain(); } catch { }
        }

        private static void PruneOldLogs()
        {
            try
            {
                foreach (string file in Directory.GetFiles(_directory, "service-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                        File.Delete(file);
                }
            }
            catch { }
        }

        private static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"(key=)[A-Za-z0-9_\-]+", "$1[redacted]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"(x-goog-api-key[""']?\s*[:=]\s*[""']?)[A-Za-z0-9_\-]+", "$1[redacted]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"AIza[0-9A-Za-z_\-]{20,}", "[redacted-api-key]");
            return text;
        }

        private static string JsonEscape(string text)
        {
            if (text == null) return "null";
            var sb = new StringBuilder(text.Length + 8);
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
