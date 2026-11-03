using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AnotherDay.Service.Domain;

namespace AnotherDay.Service.Ai
{
    public sealed class GeminiProvider : IAiProvider, IVoiceProvider
    {
        private readonly GeminiClient _client;
        private readonly AppConfig _config;

        public GeminiProvider(GeminiClient client, AppConfig config)
        {
            _client = client;
            _config = config;
        }

        public bool IsConfigured => _client.IsConfigured;
        public bool AuthRejected => _client.AuthRejected;
        public string Name => "gemini";

        public async Task<AiConversationResult> ConverseAsync(ConversationContext context, CancellationToken ct)
        {
            string prompt = new PromptBuilder().BuildConversationPrompt(context);
            using JsonDocument doc = await _client.CreateInteractionAsync(BuildTextRequest(prompt, PromptBuilder.SchemaJson), "chat", ct).ConfigureAwait(false);
            string text = ExtractOutputText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("Gemini returned an empty response.");
            return ParseConversation(text, prompt.Length);
        }

        public async Task<AiGroupResult> ConverseGroupAsync(GroupContext context, CancellationToken ct)
        {
            string prompt = new PromptBuilder().BuildGroupPrompt(context);
            using JsonDocument doc = await _client.CreateInteractionAsync(BuildTextRequest(prompt, PromptBuilder.GroupSchemaJson), "group_chat", ct).ConfigureAwait(false);
            string text = ExtractOutputText(doc.RootElement);
            if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("Gemini returned an empty group response.");
            return ParseGroup(text, prompt.Length);
        }

        public async Task<byte[]> SynthesizeAsync(string text, string voiceId, string style, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is empty.", nameof(text));
            if (string.IsNullOrWhiteSpace(voiceId)) voiceId = "Kore";
            text = text.Length > 480 ? text.Substring(0, 480) : text;

            var annotations = new List<object>();
            if (!string.IsNullOrWhiteSpace(style))
                annotations.Add(new Dictionary<string, object> { ["type"] = "speech_metadata", ["style"] = style });

            var content = new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = text
            };
            if (annotations.Count > 0) content["annotations"] = annotations;

            var body = new Dictionary<string, object>
            {
                ["model"] = _client.TtsModel,
                ["input"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "user_input",
                        ["content"] = new object[] { content }
                    }
                },
                ["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "audio",
                    ["mime_type"] = "audio/wav",
                    ["sample_rate"] = 24000
                },
                ["generation_config"] = new Dictionary<string, object>
                {
                    ["speech_config"] = new object[]
                    {
                        new Dictionary<string, object> { ["voice"] = voiceId }
                    }
                },
                ["store"] = false
            };

            using JsonDocument doc = await _client.CreateInteractionAsync(body, "tts", ct).ConfigureAwait(false);
            string base64 = ExtractAudioBase64(doc.RootElement);
            if (string.IsNullOrWhiteSpace(base64)) throw new AiUnavailableException("Gemini TTS returned no audio.");
            byte[] audio = Convert.FromBase64String(base64);
            return WavUtil.EnsureWav(audio);
        }

        public async Task<string> TranscribeAsync(string wavPath, CancellationToken ct)
        {
            if (!File.Exists(wavPath)) throw new FileNotFoundException("Recording not found.", wavPath);
            string uri = await _client.UploadFileAsync(wavPath, "audio/wav", ct).ConfigureAwait(false);
            string fileName = ExtractFileName(uri);

            try
            {
                var body = new Dictionary<string, object>
                {
                    ["model"] = _client.TranscribeModel,
                    ["input"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "audio",
                            ["uri"] = uri,
                            ["mime_type"] = "audio/wav"
                        }
                    },
                    ["generation_config"] = new Dictionary<string, object>
                    {
                        ["transcription_config"] = new Dictionary<string, object>
                        {
                            ["language_codes"] = new object[0]
                        }
                    },
                    ["store"] = false
                };

                using JsonDocument doc = await _client.CreateInteractionAsync(body, "transcribe", ct).ConfigureAwait(false);
                string text = ExtractOutputText(doc.RootElement);
                if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("Gemini returned an empty transcript.");
                return text.Trim();
            }
            finally
            {
                if (_config.Gemini.StoreInteractions == false)
                    await _client.DeleteFileAsync(fileName, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private object BuildTextRequest(string prompt, string schemaJson)
        {
            JsonNode schema = JsonNode.Parse(schemaJson);
            return new Dictionary<string, object>
            {
                ["model"] = _client.TextModel,
                ["input"] = prompt,
                ["store"] = _config.Gemini.StoreInteractions,
                ["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["mime_type"] = "application/json",
                    ["schema"] = schema
                },
                ["generation_config"] = new Dictionary<string, object>
                {
                    ["temperature"] = _config.Gemini.Temperature,
                    ["max_output_tokens"] = _config.Gemini.MaxOutputTokens
                }
            };
        }

        private static AiConversationResult ParseConversation(string raw, int promptChars)
        {
            string json = CleanJson(raw);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string dialogue = GetString(root, "dialogue");
            if (string.IsNullOrWhiteSpace(dialogue))
                throw new AiUnavailableException("Gemini response contained no dialogue.");
            if (dialogue.Length > 900) dialogue = dialogue.Substring(0, 900);

            var result = new AiConversationResult
            {
                Dialogue = dialogue.Trim(),
                InnerThought = Trim(GetString(root, "inner_thought"), 400),
                WantsToEnd = GetBool(root, "wants_to_end"),
                EstimatedTokens = (promptChars + raw.Length) / 4,
                Raw = raw
            };

            if (root.TryGetProperty("emotion_delta", out JsonElement ed) && ed.ValueKind == JsonValueKind.Object)
            {
                result.EmotionDelta = new EmotionDelta
                {
                    Happiness = GetFloat(ed, "happiness"),
                    Anger = GetFloat(ed, "anger"),
                    Fear = GetFloat(ed, "fear"),
                    Stress = GetFloat(ed, "stress"),
                    Sadness = GetFloat(ed, "sadness"),
                    Trust = GetFloat(ed, "trust"),
                    Confidence = GetFloat(ed, "confidence"),
                    Frustration = GetFloat(ed, "frustration")
                };
            }

            if (root.TryGetProperty("relationship_delta", out JsonElement rd) && rd.ValueKind == JsonValueKind.Object)
            {
                result.RelationshipDelta = new RelationshipDelta
                {
                    Trust = GetFloat(rd, "trust"),
                    Affinity = GetFloat(rd, "affinity"),
                    Fear = GetFloat(rd, "fear"),
                    Respect = GetFloat(rd, "respect")
                };
            }

            if (root.TryGetProperty("memories", out JsonElement mems) && mems.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in mems.EnumerateArray().Take(3))
                {
                    string content = Trim(GetString(m, "content"), 300);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    result.Memories.Add(new AiMemoryProposal
                    {
                        Content = content,
                        Importance = Clamp01(GetFloat(m, "importance", 0.4f)),
                        Kind = SanitizeKind(GetString(m, "kind")),
                        SubjectNpcId = Trim(GetString(m, "subject_npc_id"), 80)
                    });
                }
            }

            if (root.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in actions.EnumerateArray().Take(3))
                {
                    string type = GetString(a, "type");
                    if (!Shared.ActionTypes.IsValid(type) || string.Equals(type, Shared.ActionTypes.Speak, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Actions.Add(new AiActionProposal
                    {
                        Type = type.ToLowerInvariant(),
                        Target = Trim(GetString(a, "target"), 80),
                        Reason = Trim(GetString(a, "reason"), 200),
                        Priority = (int)Math.Clamp(GetFloat(a, "priority", 3), 1, 5),
                        DurationMs = (int)Math.Clamp(GetFloat(a, "duration_ms", 4000), 500, 30000)
                    });
                }
            }

            return result;
        }

        private static AiGroupResult ParseGroup(string raw, int promptChars)
        {
            string json = CleanJson(raw);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            var result = new AiGroupResult { Raw = raw, EstimatedTokens = (promptChars + raw.Length) / 4 };

            if (!root.TryGetProperty("turns", out JsonElement turns) || turns.ValueKind != JsonValueKind.Array)
                throw new AiUnavailableException("Gemini group response contained no turns.");

            foreach (JsonElement t in turns.EnumerateArray().Take(5))
            {
                string speaker = Trim(GetString(t, "speaker_npc_id"), 80);
                string text = Trim(GetString(t, "text"), 500);
                if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(text)) continue;

                var turn = new AiGroupTurn
                {
                    SpeakerNpcId = speaker,
                    Text = text,
                    InnerThought = Trim(GetString(t, "inner_thought"), 300)
                };
                if (t.TryGetProperty("emotion_delta", out JsonElement ed) && ed.ValueKind == JsonValueKind.Object)
                {
                    turn.EmotionDelta = new EmotionDelta
                    {
                        Happiness = GetFloat(ed, "happiness"),
                        Anger = GetFloat(ed, "anger"),
                        Fear = GetFloat(ed, "fear"),
                        Stress = GetFloat(ed, "stress"),
                        Sadness = GetFloat(ed, "sadness"),
                        Trust = GetFloat(ed, "trust"),
                        Confidence = GetFloat(ed, "confidence"),
                        Frustration = GetFloat(ed, "frustration")
                    };
                }
                result.Turns.Add(turn);
            }

            if (result.Turns.Count == 0) throw new AiUnavailableException("Gemini group response contained no usable turns.");

            if (root.TryGetProperty("memories", out JsonElement mems) && mems.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in mems.EnumerateArray().Take(4))
                {
                    string content = Trim(GetString(m, "content"), 300);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    result.Memories.Add(new AiMemoryProposal
                    {
                        Content = content,
                        Importance = Clamp01(GetFloat(m, "importance", 0.35f)),
                        Kind = SanitizeKind(GetString(m, "kind")),
                        SubjectNpcId = Trim(GetString(m, "subject_npc_id"), 80)
                    });
                }
            }
            return result;
        }

        public static string ExtractOutputText(JsonElement root)
        {
            if (root.TryGetProperty("output_text", out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();

            if (root.TryGetProperty("outputs", out JsonElement outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement output in outputs.EnumerateArray())
                {
                    if (output.ValueKind == JsonValueKind.Object &&
                        output.TryGetProperty("type", out JsonElement type) &&
                        type.GetString() == "text" &&
                        output.TryGetProperty("text", out JsonElement text))
                        return text.GetString();
                }
            }

            if (root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    if (step.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out JsonElement t) && t.GetString() == "text" &&
                                item.TryGetProperty("text", out JsonElement text))
                                builder.AppendLine(text.GetString());
                        }
                    }
                }
                if (builder.Length > 0) return builder.ToString();
            }

            return null;
        }

        public static string ExtractAudioBase64(JsonElement root)
        {
            if (root.TryGetProperty("output_audio", out JsonElement oa) && oa.ValueKind == JsonValueKind.Object &&
                oa.TryGetProperty("data", out JsonElement d0))
                return d0.GetString();

            if (root.TryGetProperty("outputs", out JsonElement outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement output in outputs.EnumerateArray())
                {
                    if (output.ValueKind == JsonValueKind.Object &&
                        output.TryGetProperty("type", out JsonElement type) &&
                        type.GetString() == "audio" &&
                        output.TryGetProperty("data", out JsonElement data))
                        return data.GetString();
                }
            }

            if (root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    if (step.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out JsonElement t) && t.GetString() == "audio" &&
                                item.TryGetProperty("data", out JsonElement data))
                                return data.GetString();
                        }
                    }
                }
            }
            return null;
        }

        private static string ExtractFileName(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;
            int idx = uri.IndexOf("/files/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return "files/" + uri.Substring(idx + 7).Trim('/');
            return null;
        }

        private static string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new AiUnavailableException("Empty JSON response.");
            string text = raw.Trim();
            if (text.StartsWith("```"))
            {
                int firstNewline = text.IndexOf('\n');
                if (firstNewline > 0) text = text.Substring(firstNewline + 1);
                if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
            }
            text = text.Trim();
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new AiUnavailableException("Gemini response was not JSON.");
            return text.Substring(start, end - start + 1);
        }

        private static string GetString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (!element.TryGetProperty(name, out JsonElement value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool GetBool(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) return false;
            return value.ValueKind == JsonValueKind.True;
        }

        private static float GetFloat(JsonElement element, string name, float fallback = 0)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float f)) return f;
            if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), out float parsed)) return parsed;
            return fallback;
        }

        private static float Clamp01(float v) => Math.Min(1f, Math.Max(0f, v));
        private static string Trim(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max);

        private static string SanitizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return "conversation";
            kind = kind.Trim().ToLowerInvariant();
            if (kind.Length > 30) kind = kind.Substring(0, 30);
            return kind;
        }
    }

    public static class WavUtil
    {
        /// <summary>Ensures the payload is a RIFF/WAVE file; wraps raw 24kHz mono PCM if necessary.</summary>
        public static byte[] EnsureWav(byte[] audio)
        {
            if (audio == null || audio.Length < 12) return audio;
            if (audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F') return audio;

            const int sampleRate = 24000;
            const short channels = 1;
            const short bits = 16;
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + audio.Length);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(audio.Length);
            writer.Write(audio);
            return ms.ToArray();
        }
    }
}
