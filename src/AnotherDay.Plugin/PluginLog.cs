using System;
using System.IO;
using System.Text;

namespace AnotherDay.Plugin
{
    internal static class PluginLog
    {
        private static readonly object Sync = new object();
        private static string _directory = DefaultDirectory();
        private static bool _debug;

        // Falls back to the standard data directory so a log line written before
        // Initialize() (for example from a failing constructor) is never lost.
        private static string DefaultDirectory()
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AnotherDayInGTA", "logs");
            }
            catch { return null; }
        }

        public static void Initialize(string directory, bool debug)
        {
            _directory = directory;
            _debug = debug;
            try { Directory.CreateDirectory(directory); } catch { }
        }

        public static void Debug(string message) { if (_debug) Write("DEBUG", message); }
        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try
            {
                lock (Sync)
                {
                    if (!string.IsNullOrEmpty(_directory))
                    {
                        Directory.CreateDirectory(_directory);
                        File.AppendAllText(Path.Combine(_directory, "plugin-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                            line + Environment.NewLine, new UTF8Encoding(false));
                    }
                }
            }
            catch { }
        }
    }
}
