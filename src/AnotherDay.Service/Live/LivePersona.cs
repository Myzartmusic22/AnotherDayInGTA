using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AnotherDay.Service.Domain;
using AnotherDay.Shared;

namespace AnotherDay.Service.Live
{
    /// <summary>
    /// Turns an NPC's stored identity into the system instruction of a Live
    /// session. This is what stops every character from sounding like the same
    /// helpful assistant: personality, mood, memories, relationship and the
    /// street outside all end up in the prompt, and the wording rules keep the
    /// model in spoken, in-character dialogue.
    /// </summary>
    public static class LivePersona
    {
        public static string Build(NpcProfile profile, EmotionState emotion, Relationship relationship,
            IEnumerable<MemoryItem> memories, IEnumerable<ConversationTurn> recentTurns,
            string zone, string weather, int clockHour, bool playerArmed, bool playerAiming,
            bool npcInVehicle = false, bool npcIsDriver = false, string npcVehicleModel = null,
            bool npcOnRoad = false, bool npcVehiclesNearby = false)
        {
            if (profile == null) return "";

            var sb = new StringBuilder();
            sb.Append("You are ").Append(profile.DisplayName);
            if (profile.Age > 0) sb.Append(", ").Append(profile.Age).Append(" years old");
            if (!string.IsNullOrWhiteSpace(profile.Occupation)) sb.Append(", a ").Append(profile.Occupation);
            sb.Append(". You are a real person standing in Los Santos, not an assistant, narrator or character sheet.\n\n");
            sb.Append("Your listed category and occupation are your real identity. They override any conflicting old memory; never claim a different job.\n\n");
            sb.Append("The game has not supplied a reliable description of your clothes, accessories, or exact employer. "
                + "Never invent a vest, uniform, hard hat, tools, workplace, or other visible detail. "
                + "If asked about something the game has not identified, say you are not sure rather than guessing.\n\n");

            sb.Append("How you speak: this is a live spoken conversation over the street noise. Use your own words, "
                + "contractions, short sentences and natural pauses. One or two sentences per reply unless you are asked to explain something. "
                + "Let your role, neighborhood, temperament and personal history shape your word choice and cadence. Use colloquial language "
                + "only when it fits this individual; do not infer a dialect or accent from race or appearance, and do not caricature anyone. "
                + "Never describe actions or feelings in stage directions, never read out punctuation, never say you are an AI, a model, "
                + "a program or a game character, and never mention memories, prompts or instructions.\n\n");

            sb.Append(DialogueLanguage.EnglishRule).Append("\n\n");

            // Engagement is spoken, so it has to be instructed explicitly.
            sb.Append("You are standing here with this person in the middle of a conversation. Stay with them: do not walk off, "
                + "do not get into a car or out of one, and do not casually announce that you are leaving. Listen until they finish, answer, "
                + "then wait for their next question. Continue through silences. If they clearly say goodbye or tell you to take care, "
                + "answer with one brief natural farewell and do not start a new topic. If you genuinely want to end the conversation, "
                + "say a clear, brief goodbye and stop speaking; do not claim to walk away or take an action the game does not perform.\n\n");

            if (npcInVehicle)
            {
                string car = string.IsNullOrWhiteSpace(npcVehicleModel) ? "car" : npcVehicleModel;
                sb.Append(npcIsDriver
                        ? "You are in the driver's seat of your " + car + ", engine running, talking to this person through the window. "
                          + "Stay at the wheel: do not get out, do not put it in gear and drive off while they are still talking to you. "
                        : "You are in the passenger seat of a " + car + ", talking through the window. Stay in the seat. ")
                  .Append("If they ask for a ride, decide in character - agree and tell them to get in, refuse, or say you are busy - ")
                  .Append("and answer from where you are sitting.\n\n");
            }

            string voiceStyle = profile.Voice != null && !string.IsNullOrWhiteSpace(profile.Voice.Style)
                ? profile.Voice.Style
                : VoiceEngine.BuildStyle(profile, emotion);
            if (!string.IsNullOrWhiteSpace(voiceStyle))
                sb.Append("Your delivery: ").Append(voiceStyle).Append(".\n\n");

            if (!string.IsNullOrWhiteSpace(profile.PersonalityDescription))
                sb.Append("Who you are: ").Append(profile.PersonalityDescription).Append('\n');
            if (!string.IsNullOrWhiteSpace(profile.Background))
                sb.Append("Your history: ").Append(profile.Background).Append('\n');

            PersonalityTraits traits = profile.Personality;
            if (traits != null)
            {
                sb.Append("Temperament (0 = none, 1 = extreme): friendly ").Append(Score(traits.Friendliness))
                  .Append(", aggressive ").Append(Score(traits.Aggression))
                  .Append(", confident ").Append(Score(traits.Confidence))
                  .Append(", suspicious ").Append(Score(traits.Suspicion))
                  .Append(", sociable ").Append(Score(traits.Sociability))
                  .Append(", humorous ").Append(Score(traits.Humor))
                  .Append(", impulsive ").Append(Score(traits.Impulsiveness)).Append(".\n");
            }

            if (profile.Interests != null && profile.Interests.Count > 0)
                sb.Append("You care about: ").Append(string.Join(", ", profile.Interests.Take(4))).Append(".\n");
            if (profile.Quirks != null && profile.Quirks.Count > 0)
                sb.Append("Speaking habits: ").Append(string.Join("; ", profile.Quirks.Take(3))).Append(".\n");

            sb.Append('\n');
            sb.Append("Right now you feel ").Append(emotion != null ? EmotionWords(emotion) : "ordinary").Append(". ");
            sb.Append("Act on that mood, do not announce it.\n");

            if (relationship != null)
            {
                sb.Append("The person talking to you is ").Append(RelationshipWords(relationship));
                if (!string.IsNullOrWhiteSpace(relationship.Notes))
                    sb.Append(" (you two have history: ").Append(Trim(relationship.Notes, 200)).Append(")");
                sb.Append(".\n");
            }

            string place = string.IsNullOrWhiteSpace(zone) ? "Los Santos" : zone;
            sb.Append("You are in ").Append(place).Append(", weather ").Append(string.IsNullOrWhiteSpace(weather) ? "clear" : weather)
              .Append(", ").Append(ClockWords(clockHour)).Append(". ");
            if (playerArmed || playerAiming)
                sb.Append(playerAiming
                    ? "The player is aiming a weapon at you and you can see it."
                    : "The player is carrying a visible weapon.");
            else
                sb.Append("Nothing about the player looks threatening right now.");
            sb.Append('\n');

            if (npcOnRoad || npcVehiclesNearby)
            {
                sb.Append("\nWhat you can see around you: ");
                if (npcOnRoad) sb.Append("you are standing on a road");
                if (npcOnRoad && npcVehiclesNearby) sb.Append(" and ");
                if (npcVehiclesNearby) sb.Append("vehicles are nearby");
                sb.Append(". Acknowledge this scene naturally if it matters or the other person points it out. "
                    + "Do not claim to have moved or taken an action the game does not perform.\n");
            }

            var memoryLines = (memories ?? Enumerable.Empty<MemoryItem>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content))
                .Take(8)
                .Select(m => "- " + Trim(m.Content, 220))
                .ToList();
            if (memoryLines.Count > 0)
                sb.Append("\nWhat you remember:\n").Append(string.Join("\n", memoryLines)).Append('\n');

            var turnLines = (recentTurns ?? Enumerable.Empty<ConversationTurn>())
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text))
                .TakeLast(6)
                .Select(t => "- " + (string.Equals(t.Speaker, profile.NpcId, StringComparison.OrdinalIgnoreCase) ? "you said: " : "they said: ") + Trim(t.Text, 180))
                .ToList();
            if (turnLines.Count > 0)
                sb.Append("\nEarlier in this conversation:\n").Append(string.Join("\n", turnLines)).Append('\n');

            sb.Append("\nStay in character. If you do not know something, react to it the way this person would instead of inventing facts about the city. "
                + "If the player interrupts you, stop and listen. Never repeat a line you have already used, and do not echo or deliberately repeat the last word of a sentence. "
                + "Speak English in every reply, even if the player speaks another language or their English is rough.");

            return sb.ToString();
        }

        private static string Score(float value) => Math.Round(value, 2).ToString("0.00");

        private static string Trim(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + "...";

        public static string EmotionWords(EmotionState e)
        {
            var mood = new List<string>();
            if (e.Anger > 0.6f) mood.Add("angry");
            else if (e.Frustration > 0.6f) mood.Add("irritated");
            if (e.Fear > 0.55f) mood.Add("scared");
            if (e.Sadness > 0.6f) mood.Add("low");
            if (e.Happiness > 0.65f) mood.Add("in a good mood");
            if (e.Stress > 0.6f) mood.Add("wound up");
            if (e.Confidence > 0.7f) mood.Add("sure of yourself");
            if (e.Trust < 0.25f) mood.Add("wary of this person");
            if (mood.Count == 0) mood.Add("fairly even");
            return string.Join(", ", mood.Distinct());
        }

        private static string RelationshipWords(Relationship r)
        {
            if (r.Fear > 0.6f) return "someone you are afraid of";
            if (r.Affinity > 0.6f && r.Trust > 0.5f) return "a friend of yours";
            if (r.Affinity < -0.5f) return "someone you dislike";
            if (r.Trust < -0.3f) return "someone you do not trust";
            if (r.Familiarity > 0.45f) return "someone you have met before, an acquaintance";
            return "a stranger who just walked up to you";
        }

        private static string ClockWords(int hour)
        {
            if (hour < 5) return "the small hours of the night";
            if (hour < 9) return "early morning";
            if (hour < 12) return "late morning";
            if (hour < 15) return "the middle of the day";
            if (hour < 18) return "the afternoon";
            if (hour < 21) return "the evening";
            return "late at night";
        }
    }
}
