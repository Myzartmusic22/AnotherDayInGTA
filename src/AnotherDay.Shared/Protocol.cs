using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherDay.Shared
{
    public static class Protocol
    {
        public const int SchemaVersion = 1;
        public const int MaxFrameBytes = 16 * 1024 * 1024;
        public const string DefaultPipeName = "AnotherDayInGTA";

        /// <summary>Live voice conversations use their own duplex pipe (mainName + this suffix).</summary>
        public const string VoicePipeSuffix = ".voice";

        public static string VoicePipeName(string mainPipeName) =>
            (string.IsNullOrWhiteSpace(mainPipeName) ? DefaultPipeName : mainPipeName) + VoicePipeSuffix;
        public const string ProductName = "Another Day in GTA";
    }

    public static class MessageTypes
    {
        public const string Hello = "hello";
        public const string HelloResult = "hello.result";
        public const string PedSnapshot = "peds.snapshot";
        public const string PedSnapshotResult = "peds.snapshot.result";
        public const string Converse = "conversation.say";
        public const string ConverseResult = "conversation.say.result";
        public const string VoiceConverse = "conversation.voice";
        public const string VoiceConverseResult = "conversation.voice.result";
        public const string WorldEvents = "world.events";
        public const string WorldEventsResult = "world.events.result";
        public const string NpcSpeech = "npc.speech";
        public const string ActionCommand = "npc.action";
        public const string GetSettings = "settings.get";
        public const string SettingsResult = "settings.get.result";
        public const string UpdateSettings = "settings.update";
        public const string UpdateSettingsResult = "settings.update.result";
        public const string GetStatus = "status.get";
        public const string StatusResult = "status.get.result";
        public const string TriggerSocial = "social.trigger";
        public const string TriggerSocialResult = "social.trigger.result";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Error = "error";
    }

    /// <summary>
    /// Every IPC message is wrapped in this envelope. Payloads are serialized
    /// separately so the service (System.Text.Json) and the plugin
    /// (Newtonsoft.Json) can share POCO contracts without serializer coupling.
    /// </summary>
    public sealed class MessageEnvelope
    {
        public int SchemaVersion { get; set; } = Protocol.SchemaVersion;
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
        public string CorrelationId { get; set; }
        public string Type { get; set; }
        public string SentUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public string PayloadJson { get; set; } = "{}";
    }

    public static class FrameCodec
    {
        public static async Task WriteFrameAsync(Stream stream, MessageEnvelope envelope, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));

            string json = Serialization.SerializeEnvelope(envelope);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            if (payload.Length > Protocol.MaxFrameBytes)
                throw new InvalidOperationException("IPC frame exceeds the maximum allowed size.");

            byte[] header = new byte[4];
            header[0] = (byte)(payload.Length & 0xFF);
            header[1] = (byte)((payload.Length >> 8) & 0xFF);
            header[2] = (byte)((payload.Length >> 16) & 0xFF);
            header[3] = (byte)((payload.Length >> 24) & 0xFF);

            await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<MessageEnvelope> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            byte[] header = new byte[4];
            if (!await ReadExactAsync(stream, header, 0, 4, ct).ConfigureAwait(false))
                return null;

            int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length <= 0 || length > Protocol.MaxFrameBytes)
                throw new InvalidDataException("Invalid IPC frame length: " + length);

            byte[] payload = new byte[length];
            if (!await ReadExactAsync(stream, payload, 0, length, ct).ConfigureAwait(false))
                return null;

            string json = Encoding.UTF8.GetString(payload);
            MessageEnvelope envelope = Serialization.DeserializeEnvelope(json);
            if (envelope == null) throw new InvalidDataException("Empty IPC envelope.");
            if (envelope.SchemaVersion != Protocol.SchemaVersion)
                throw new InvalidDataException("Unsupported IPC schema version: " + envelope.SchemaVersion);
            if (string.IsNullOrWhiteSpace(envelope.Type))
                throw new InvalidDataException("IPC envelope has no message type.");
            return envelope;
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
                if (read <= 0) return false;
                total += read;
            }
            return true;
        }
    }

    /// <summary>
    /// Minimal envelope serialization with no third-party dependency. Payloads
    /// are opaque strings and are validated by the receiver.
    /// </summary>
    internal static class Serialization
    {
        public static string SerializeEnvelope(MessageEnvelope e)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendNumber(sb, "schemaVersion", e.SchemaVersion); sb.Append(',');
            AppendString(sb, "messageId", e.MessageId); sb.Append(',');
            AppendString(sb, "correlationId", e.CorrelationId); sb.Append(',');
            AppendString(sb, "type", e.Type); sb.Append(',');
            AppendString(sb, "sentUtc", e.SentUtc); sb.Append(',');
            AppendString(sb, "payloadJson", e.PayloadJson ?? "{}");
            sb.Append('}');
            return sb.ToString();
        }

        public static MessageEnvelope DeserializeEnvelope(string json)
        {
            // System.Text.Json is not referenced by this netstandard2.0 assembly
            // so the envelope parser is intentionally tiny and strict.
            var e = new MessageEnvelope();
            e.SchemaVersion = (int)ReadNumber(json, "schemaVersion", Protocol.SchemaVersion);
            e.MessageId = ReadString(json, "messageId");
            e.CorrelationId = ReadString(json, "correlationId");
            e.Type = ReadString(json, "type");
            e.SentUtc = ReadString(json, "sentUtc");
            e.PayloadJson = ReadString(json, "payloadJson") ?? "{}";
            return e;
        }

        private static void AppendString(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":");
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in value)
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
        }

        private static void AppendNumber(StringBuilder sb, string name, int value)
        {
            sb.Append('"').Append(name).Append("\":").Append(value);
        }

        private static string ReadString(string json, string name)
        {
            int idx = FindValue(json, name);
            if (idx < 0) return null;
            if (json[idx] == 'n') return null;
            if (json[idx] != '"') return null;
            idx++;
            var sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && idx < json.Length)
                {
                    char esc = json[idx++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (idx + 4 <= json.Length &&
                                int.TryParse(json.Substring(idx, 4), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                            { sb.Append((char)cp); idx += 4; }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static double ReadNumber(string json, string name, double fallback)
        {
            int idx = FindValue(json, name);
            if (idx < 0) return fallback;
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-' || json[idx] == '.' || json[idx] == '+' || json[idx] == 'e' || json[idx] == 'E')) idx++;
            if (idx <= start) return fallback;
            return double.TryParse(json.Substring(start, idx - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private static int FindValue(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            string needle = "\"" + name + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += needle.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return -1;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            return i;
        }
    }
}
