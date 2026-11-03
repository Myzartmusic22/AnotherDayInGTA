using System;
using System.IO;

namespace AnotherDay.Service
{
    public sealed class AppPaths
    {
        public string Root { get; }
        public string DatabasePath => Path.Combine(Root, "anotherday.db");
        public string ConfigPath => Path.Combine(Root, "config.json");
        public string SecretPath => Path.Combine(Root, "gemini.key");
        public string TokenPath => Path.Combine(Root, "ipc.token");
        public string AudioCacheDirectory => Path.Combine(Root, "audio_cache");
        public string LogDirectory => Path.Combine(Root, "logs");
        public string BackupDirectory => Path.Combine(Root, "backups");
        public string PluginConfigPath => Path.Combine(Root, "plugin.json");
        public string CrashDirectory => Path.Combine(Root, "crashes");

        public AppPaths(string root = null)
        {
            string overridden = Environment.GetEnvironmentVariable("ANOTHERDAY_DATA_DIR");
            Root = root
                ?? (!string.IsNullOrWhiteSpace(overridden)
                    ? Path.GetFullPath(overridden)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDayInGTA"));
        }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(AudioCacheDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(CrashDirectory);
        }
    }
}
