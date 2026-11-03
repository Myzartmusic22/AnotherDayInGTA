using System;
using System.Diagnostics;
using System.IO;

namespace AnotherDay.Plugin.Ipc
{
    internal static class ServiceLauncher
    {
        private static DateTime _lastAttemptUtc = DateTime.MinValue;
        private static readonly object Sync = new object();

        public static void TryStart(PluginConfig config)
        {
            if (!config.AutoStartService) return;
            lock (Sync)
            {
                if ((DateTime.UtcNow - _lastAttemptUtc).TotalSeconds < 45) return;
                _lastAttemptUtc = DateTime.UtcNow;

                string exe = config.ResolveServiceExe();
                if (string.IsNullOrEmpty(exe))
                {
                    PluginLog.Warn("AI service executable was not found. Start AnotherDay.Service.exe manually.");
                    return;
                }

                try
                {
                    var info = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "--data \"" + config.ResolvedDataDirectory + "\" --pipe \"" + config.PipeName + "\"",
                        WorkingDirectory = Path.GetDirectoryName(exe),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process process = Process.Start(info);
                    PluginLog.Info("Started AI service (PID " + (process?.Id.ToString() ?? "unknown") + ").");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Could not start AI service: " + ex.Message);
                }
            }
        }
    }
}
