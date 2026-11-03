using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AnotherDay.Service.Security
{
    /// <summary>
    /// Stores the Gemini API key with Windows DPAPI (current user scope). The
    /// key is never written to config.json or logs. An environment variable
    /// (GEMINI_API_KEY or GOOGLE_API_KEY) takes precedence for CI/testing.
    /// </summary>
    public sealed class SecretStore
    {
        private readonly AppPaths _paths;
        private string _cached;

        public SecretStore(AppPaths paths)
        {
            _paths = paths;
        }

        public string GetApiKey()
        {
            if (!string.IsNullOrWhiteSpace(_cached)) return _cached;

            string env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(env)) env = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (!string.IsNullOrWhiteSpace(env))
            {
                _cached = Sanitize(env);
                WarnIfMalformed(_cached, "the GEMINI_API_KEY environment variable");
                return _cached;
            }

            try
            {
                if (File.Exists(_paths.SecretPath))
                {
                    byte[] encrypted = File.ReadAllBytes(_paths.SecretPath);
                    byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    _cached = Sanitize(Encoding.UTF8.GetString(plain));
                    if (string.IsNullOrWhiteSpace(_cached)) return null;
                    WarnIfMalformed(_cached, "the stored key");
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read protected API key: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Removes what a clipboard or terminal adds around a key: byte-order
        /// marks, zero-width and other non-ASCII characters, quotes, tabs and
        /// trailing spaces. A key is a visible-ASCII token, so anything else is
        /// noise that would otherwise be sent to Google verbatim and rejected.
        /// </summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c > 32 && c < 127) sb.Append(c);
            }
            return sb.ToString().Trim('"', '\'', ' ');
        }

        /// <summary>
        /// Both key families have one dot at most ("AQ." + payload) or none
        /// ("AIza..."). Two dots means two keys or a doubled paste, which Google
        /// answers with a plain 401 - the exact failure that looked like a bad
        /// key. Reported with a fingerprint, never the key itself.
        /// </summary>
        public static bool LooksMalformed(string key, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(key)) { reason = "it is empty"; return true; }
            if (key.Length > 120) { reason = "it is " + key.Length + " characters long (a key is 20-120)"; return true; }
            int dots = key.Count(c => c == '.');
            if (dots > 1) { reason = "it contains " + dots + " dots (a key has at most one)"; return true; }
            foreach (char c in key)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '_' || c == '-';
                if (!ok) { reason = "it contains a character a key never has"; return true; }
            }
            return false;
        }

        private static void WarnIfMalformed(string key, string source)
        {
            if (LooksMalformed(key, out string reason))
                Log.Warn("The Gemini API key from " + source + " looks malformed: " + reason +
                         ". Fingerprint " + Fingerprint(key) + ". Re-run AnotherDay.Service.exe --set-key with the key copied again.");
        }

        /// <summary>Short, non-reversible identifier for logs and diagnostics.</summary>
        public static string Fingerprint(string key)
        {
            if (string.IsNullOrEmpty(key)) return "none";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash).Substring(0, 8) + "/" + key.Length;
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(GetApiKey());

        public void SetApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is empty.", nameof(apiKey));
            apiKey = Sanitize(apiKey);
            if (apiKey.Length < 20) throw new ArgumentException("That does not look like a Gemini API key.", nameof(apiKey));
            if (LooksMalformed(apiKey, out string reason))
                throw new ArgumentException("That does not look like a single Gemini API key: " + reason +
                    ". Copy the key once, without quotes or line breaks.", nameof(apiKey));
            byte[] plain = Encoding.UTF8.GetBytes(apiKey);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.SecretPath));
            File.WriteAllBytes(_paths.SecretPath, encrypted);
            try
            {
                var info = new FileInfo(_paths.SecretPath);
                info.Attributes |= FileAttributes.Hidden;
            }
            catch { }
            _cached = apiKey;
            Log.Info("Gemini API key stored with Windows DPAPI protection.");
        }

        public void ClearApiKey()
        {
            _cached = null;
            try { if (File.Exists(_paths.SecretPath)) File.Delete(_paths.SecretPath); }
            catch (Exception ex) { Log.Warn("Could not delete stored API key: " + ex.Message); }
        }

        public string MaskedStatus => HasApiKey ? "configured (hidden)" : "not configured";
    }
}
