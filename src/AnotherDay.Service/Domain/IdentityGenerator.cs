using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AnotherDay.Shared;

namespace AnotherDay.Service.Domain
{
    /// <summary>
    /// Deterministic identity generation. The same fingerprint always produces
    /// the same person, so a returning pedestrian keeps their history even if
    /// the service restarts before the profile is written.
    /// </summary>
    public sealed class IdentityGenerator
    {
        private static readonly string[] MaleFirst =
        {
            "Marcus", "Andre", "Darnell", "Luis", "Diego", "Tyrone", "Jamal", "Omar", "Ricky", "Vince",
            "Kevin", "Sean", "Miguel", "Antonio", "Dwayne", "Curtis", "Leon", "Malik", "Trevor", "Wesley",
            "Franklin", "Oscar", "Hector", "Ramon", "Ivan", "Dmitri", "Yusuf", "Karim", "Jin", "Minho",
            "Ethan", "Noah", "Caleb", "Miles", "Owen", "Dylan", "Logan", "Austin", "Colton", "Nate",
            "Rashid", "Kwame", "Emeka", "Sipho", "Vikram", "Arjun", "Tariq", "Hassan", "Nico", "Enzo",
            "Bobby", "Jimmy", "Ray", "Earl", "Gus", "Sal", "Tony", "Manny", "Pablo", "Chad"
        };

        private static readonly string[] FemaleFirst =
        {
            "Alisha", "Keisha", "Tanya", "Monica", "Rosa", "Lucia", "Carmen", "Sofia", "Elena", "Maya",
            "Jasmine", "Destiny", "Chloe", "Nia", "Imani", "Shanice", "Tasha", "Valerie", "Gloria", "Dana",
            "Amber", "Brittany", "Carla", "Denise", "Erica", "Felicia", "Gabriela", "Hannah", "Isabel", "Jade",
            "Kara", "Lena", "Mia", "Natalie", "Olivia", "Paula", "Quinn", "Rachel", "Sabrina", "Teresa",
            "Ursula", "Vera", "Wendy", "Yolanda", "Zoe", "Mei", "Priya", "Aisha", "Fatima", "Ingrid",
            "Anya", "Katya", "Sasha", "Nadia", "Ruth", "Edith", "Mabel", "Doris", "June", "Rita"
        };

        private static readonly string[] LastNames =
        {
            "Reyes", "Johnson", "Carter", "Mendez", "Brown", "Williams", "Garcia", "Ramirez", "Walker", "Davis",
            "Nguyen", "Patel", "Kim", "Chen", "Kowalski", "Petrov", "Okafor", "Haddad", "Silva", "Rossi",
            "Murphy", "O'Brien", "Khan", "Diallo", "Jackson", "Harris", "Lewis", "Robinson", "Clark", "Young",
            "Lopez", "Torres", "Flores", "Rivera", "Cruz", "Morales", "Ortiz", "Castillo", "Vargas", "Santos",
            "Miller", "Wilson", "Moore", "Taylor", "Anderson", "Thomas", "White", "Martin", "Thompson", "Moore",
            "Bennett", "Foster", "Hayes", "Reed", "Cole", "Brooks", "Price", "Woods", "Banks", "Sutton"
        };

        private static readonly string[] Neighborhoods =
        {
            "Davis", "Strawberry", "Chamberlain Hills", "Rancho", "Vinewood", "Rockford Hills",
            "Del Perro", "Vespucci Beach", "Little Seoul", "Downtown Los Santos", "Mission Row",
            "La Mesa", "El Burro Heights", "Mirror Park", "Sandy Shores", "Paleto Bay", "Grapeseed",
            "Elysian Island", "Port of Los Santos", "Vinewood Hills", "Chumash", "Tataviam Mountains"
        };

        private static readonly string[] InterestsPool =
        {
            "local music", "street food", "basketball", "boxing", "car culture", "fishing", "hiking",
            "old movies", "conspiracy videos", "cooking", "gardening", "photography", "gambling",
            "motorbikes", "surfing", "football", "talk radio", "true crime", "video games", "poetry",
            "community work", "religion", "money", "family", "travel", "military history", "art",
            "skateboarding", "hunting", "fashion", "cars", "the beach", "local politics", "cooking shows"
        };

        private static readonly string[] BeliefsPool =
        {
            "the city spends too much on downtown and not enough on the neighborhoods",
            "cops only show up after something bad has already happened",
            "the gangs are getting bolder every year",
            "rent is out of control and nobody in City Hall cares",
            "the rich live up in the hills and never see the real Los Santos",
            "you can't trust the news to tell you what actually happened",
            "a man should handle his own problems",
            "people need to look out for their own block first",
            "the police are doing a hard job with no help",
            "most people are decent if you give them a chance",
            "everybody's got an angle in this city",
            "the beaches are the only thing worth keeping",
            "the city was better before all the construction",
            "there are too many guns on the street",
            "if you work hard, you can still get ahead here",
            "nobody really knows what goes on in the hills",
            "the government is hiding something about the water",
            "the emergency services are underpaid",
            "the only law that matters is the one you can afford",
            "people should mind their own business",
            "the gangs keep the neighborhood safe from outsiders",
            "the police treat some neighborhoods differently",
            "the city needs more jobs, not more cops",
            "the city needs more cops, not more outreach",
            "you should never snitch, no matter what"
        };

        private static readonly string[] QuirksPool =
        {
            "taps a coin against their knuckles when thinking",
            "always mentions the weather before getting to the point",
            "laughs at their own jokes before finishing them",
            "calls everyone 'boss'",
            "checks over their shoulder when a car backfires",
            "chews on a toothpick",
            "answers questions with another question",
            "uses restaurant metaphors for everything",
            "has a strong opinion about the correct way to make coffee",
            "says 'you know what I'm saying' after every point",
            "hates being interrupted and will restart a sentence",
            "keeps a mental list of everybody who has wronged them",
            "talks with their hands",
            "never swears in front of strangers",
            "swears constantly but apologizes for it",
            "hums when nervous",
            "always knows a guy who can get something",
            "refuses to sit with their back to the door",
            "calls the city 'Los Santos' with exaggerated formality"
        };

        private static readonly Dictionary<string, string[]> Occupations = new Dictionary<string, string[]>
        {
            [NpcCategories.Civilian] = new[] { "local resident" },
            [NpcCategories.Police] = new[] { "police officer" },
            [NpcCategories.Gang] = new[] { "gang member" },
            [NpcCategories.Shopkeeper] = new[] { "shopkeeper" },
            [NpcCategories.Business] = new[] { "business professional" },
            [NpcCategories.Employee] = new[] { "employee" },
            [NpcCategories.Driver] = new[] { "driver" },
            [NpcCategories.Passenger] = new[] { "passenger" },
            [NpcCategories.Security] = new[] { "security guard" },
            [NpcCategories.Paramedic] = new[] { "paramedic" },
            [NpcCategories.Firefighter] = new[] { "firefighter" },
            [NpcCategories.Mechanic] = new[] { "mechanic" },
            [NpcCategories.Homeless] = new[] { "local resident" },
            [NpcCategories.Other] = new[] { "local resident" }
        };

        private static readonly Dictionary<string, string> CategoryBackground = new Dictionary<string, string>
        {
            [NpcCategories.Police] = "Works long shifts and has seen the city change. Believes the job is thankless but necessary, and is wary of people who act too friendly too fast.",
            [NpcCategories.Gang] = "Grew up on the same blocks they now defend. Loyal to a small circle, suspicious of outsiders, and careful about what they say in public.",
            [NpcCategories.Shopkeeper] = "Runs a small business on thin margins. Knows every regular by name and has learned to read trouble before it walks through the door.",
            [NpcCategories.Business] = "Climbs the local ladder and measures people by what they can do for them. Polished in public, calculating in private.",
            [NpcCategories.Employee] = "Works a job that pays the bills but offers little else. Tired, observant, and usually happy to complain about management.",
            [NpcCategories.Driver] = "Spends most of the day on the road. Has opinions about traffic, cops, and every shortcut in the city.",
            [NpcCategories.Passenger] = "Just passing through, or trying to get home. Not looking for trouble and not impressed by the city's reputation.",
            [NpcCategories.Security] = "Paid to watch doors and people. Treats strangers as a problem until proven otherwise and takes the job more seriously than anyone expects.",
            [NpcCategories.Paramedic] = "Sees the worst nights the city has to offer. Calm under pressure, blunt with people who waste time, and quietly exhausted.",
            [NpcCategories.Firefighter] = "Runs toward what everyone else runs from. Practical, steady, and impatient with panic.",
            [NpcCategories.Mechanic] = "Lives around engines and grease. Direct, skeptical of fancy talk, and generous with help if you show respect.",
            [NpcCategories.Homeless] = "Has been on the street long enough to know which corners are safe and which people to avoid. Guarded but not unfriendly.",
            [NpcCategories.Civilian] = "An ordinary resident trying to get through an ordinary day in a city that rarely stays ordinary.",
            [NpcCategories.Other] = "Lives on the margins of the city's routines and prefers it that way."
        };

        public NpcProfile Generate(PedObservation ped, string zone, string gameVersion, string npcId)
        {
            string fingerprint = ped.Fingerprint ?? (ped.ModelHash + ":" + ped.Handle);
            var rng = new StableRandom(HashSeed(fingerprint + "|" + (ped.IsMale ? "m" : "f") + "|" + (ped.ModelName ?? "")));
            string category = ClassifyCategory(ped);
            string neighborhood = Pick(rng, Neighborhoods);
            string occupation = Pick(rng, Occupations.TryGetValue(category, out var ops) ? ops : Occupations[NpcCategories.Civilian]);
            int age = rng.Next(18, 74);
            if (category == NpcCategories.Police) age = rng.Next(24, 58);
            if (category == NpcCategories.Gang) age = rng.Next(18, 36);

            string name = Pick(rng, ped.IsMale ? MaleFirst : FemaleFirst) + " " + Pick(rng, LastNames);
            var personality = GeneratePersonality(rng, category);
            string speechArea = string.IsNullOrWhiteSpace(zone) ? neighborhood : zone;
            var speech = GenerateSpeech(rng, category, ped.IsMale, personality, speechArea);
            var interests = PickMany(rng, InterestsPool, 3).ToList();
            var beliefs = PickMany(rng, BeliefsPool, 3).ToList();
            var quirks = PickMany(rng, QuirksPool, rng.Next(1, 3)).ToList();

            string background = BuildBackground(category, occupation, neighborhood, rng);

            return new NpcProfile
            {
                NpcId = npcId,
                Fingerprint = fingerprint,
                ModelHash = ped.ModelHash,
                ModelName = ped.ModelName ?? "",
                IsMale = ped.IsMale,
                DisplayName = name,
                Age = age,
                Occupation = occupation,
                Category = category,
                Neighborhood = string.IsNullOrWhiteSpace(zone) ? neighborhood : zone,
                Background = background,
                Personality = personality,
                PersonalityDescription = personality.ShortDescription(),
                Speech = speech,
                Interests = interests,
                Beliefs = beliefs,
                Quirks = quirks,
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                LastKnownPosition = ped.Position ?? new Vec3(),
                TimesSeen = 1,
                IdentityConfidence = 1.0,
                GameVersion = gameVersion
            };
        }

        public static string ClassifyCategory(PedObservation ped)
        {
            string model = (ped.ModelName ?? "").ToLowerInvariant();
            if (model.Contains("cop") || model.Contains("police") || model.Contains("sheriff") ||
                model.Contains("swat") || model.Contains("ranger") || model.Contains("fib") ||
                model.Contains("cia") || model.Contains("marine") || model.Contains("army"))
                return NpcCategories.Police;
            if (model.Contains("fire")) return NpcCategories.Firefighter;
            if (model.Contains("paramedic") || model.Contains("medic") || model.Contains("doctor") || model.Contains("nurse"))
                return NpcCategories.Paramedic;
            if (model.Contains("security") || model.Contains("guard")) return NpcCategories.Security;
            if (model.Contains("g_m_") || model.Contains("g_f_") || model.Contains("gang") || model.Contains("ballas") ||
                model.Contains("vagos") || model.Contains("families") || model.Contains("lost") ||
                model.Contains("azteca") || model.Contains("triad") || model.Contains("cartel") ||
                model.Contains("salvador"))
                return NpcCategories.Gang;
            if (model.Contains("shop") || model.Contains("clerk") || model.Contains("business") ||
                model.Contains("banker") || model.Contains("trader") || model.Contains("owner"))
                return NpcCategories.Shopkeeper;
            if (model.Contains("mechanic") || model.Contains("spawn") || model.Contains("tow"))
                return NpcCategories.Mechanic;
            if (model.Contains("bum") || model.Contains("homeless") || model.Contains("tramp"))
                return NpcCategories.Homeless;
            if (ped.IsInVehicle && ped.VehicleSeat == -1) return NpcCategories.Driver;
            if (ped.IsInVehicle) return NpcCategories.Passenger;
            if (!string.IsNullOrEmpty(ped.CategoryHint)) return ped.CategoryHint;
            return NpcCategories.Civilian;
        }

        private PersonalityTraits GeneratePersonality(StableRandom rng, string category)
        {
            var p = new PersonalityTraits
            {
                Friendliness = rng.Float(0.15f, 0.9f),
                Aggression = rng.Float(0.05f, 0.75f),
                Confidence = rng.Float(0.2f, 0.9f),
                Intelligence = rng.Float(0.15f, 0.95f),
                Suspicion = rng.Float(0.1f, 0.85f),
                Humor = rng.Float(0.05f, 0.95f),
                Empathy = rng.Float(0.1f, 0.9f),
                Impulsiveness = rng.Float(0.05f, 0.85f),
                Sociability = rng.Float(0.15f, 0.95f),
                Loyalty = rng.Float(0.2f, 0.95f)
            };

            switch (category)
            {
                case NpcCategories.Police:
                    p.Confidence = Clamp(p.Confidence + 0.2f);
                    p.Suspicion = Clamp(p.Suspicion + 0.2f);
                    p.Loyalty = Clamp(p.Loyalty + 0.2f);
                    p.Empathy = Clamp(p.Empathy - 0.1f);
                    break;
                case NpcCategories.Gang:
                    p.Aggression = Clamp(p.Aggression + 0.2f);
                    p.Loyalty = Clamp(p.Loyalty + 0.2f);
                    p.Suspicion = Clamp(p.Suspicion + 0.1f);
                    p.Friendliness = Clamp(p.Friendliness - 0.15f);
                    break;
                case NpcCategories.Paramedic:
                case NpcCategories.Firefighter:
                    p.Empathy = Clamp(p.Empathy + 0.25f);
                    p.Confidence = Clamp(p.Confidence + 0.1f);
                    p.Impulsiveness = Clamp(p.Impulsiveness - 0.2f);
                    break;
                case NpcCategories.Shopkeeper:
                    p.Suspicion = Clamp(p.Suspicion + 0.1f);
                    p.Friendliness = Clamp(p.Friendliness + 0.05f);
                    break;
                case NpcCategories.Homeless:
                    p.Suspicion = Clamp(p.Suspicion + 0.15f);
                    p.Confidence = Clamp(p.Confidence - 0.15f);
                    break;
            }
            return p;
        }

        private SpeechStyle GenerateSpeech(StableRandom rng, string category, bool male, PersonalityTraits p,
            string area)
        {
            var paces = new[] { "slow", "measured", "normal", "quick", "rapid" };
            string pace = paces[rng.Next(paces.Length)];
            string vocabulary = "everyday";
            string profanityLevel = "occasional";

            switch (category)
            {
                case NpcCategories.Police:
                    vocabulary = "clipped official";
                    profanityLevel = "rare";
                    pace = "measured";
                    break;
                case NpcCategories.Gang:
                    vocabulary = "streetwise";
                    profanityLevel = "occasional";
                    break;
                case NpcCategories.Business:
                    vocabulary = "polished professional";
                    profanityLevel = "rare";
                    break;
                case NpcCategories.Driver:
                case NpcCategories.Mechanic:
                    vocabulary = "practical and direct";
                    break;
                case NpcCategories.Paramedic:
                case NpcCategories.Firefighter:
                    vocabulary = "clear and practical";
                    profanityLevel = "rare";
                    break;
            }

            if (p.Sociability > 0.7f && category != NpcCategories.Police) pace = "quick";

            return new SpeechStyle
            {
                Accent = VoiceEngine.AccentForArea(area),
                Pace = pace,
                Pitch = male
                    ? (p.Confidence > 0.7f ? "low" : "medium")
                    : (p.Confidence > 0.7f ? "medium" : "high"),
                Vocabulary = vocabulary,
                Profanity = profanityLevel,
                VerbalTic = "",
                SentenceLength = p.Intelligence > 0.7f ? "varied" : (rng.NextDouble() < 0.5 ? "short" : "medium")
            };
        }

        private string BuildBackground(string category, string occupation, string neighborhood, StableRandom rng)
        {
            string baseText = CategoryBackground.TryGetValue(category, out string b) ? b : CategoryBackground[NpcCategories.Civilian];
            string[] hooks =
            {
                "Has lived around " + neighborhood + " for " + rng.Next(2, 30) + " years.",
                "Moved to " + neighborhood + " after things fell apart elsewhere.",
                "Grew up a few blocks from " + neighborhood + " and still knows the old crowd.",
                "Commutes into " + neighborhood + " every day and would rather be somewhere quieter.",
                "Knows " + neighborhood + " well enough to avoid the wrong streets at night."
            };
            string background = baseText + " " + hooks[rng.Next(hooks.Length)];
            if (!string.Equals(occupation, "local resident", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(occupation, "passenger", StringComparison.OrdinalIgnoreCase))
                background += " Their observed role is " + occupation + ".";
            else
                background += " Their exact occupation is not established.";
            return background;
        }

        private static string Pick(StableRandom rng, string[] values) => values[rng.Next(values.Length)];

        private static IEnumerable<string> PickMany(StableRandom rng, string[] values, int count)
        {
            var copy = values.ToList();
            for (int i = 0; i < count && copy.Count > 0; i++)
            {
                int idx = rng.Next(copy.Count);
                yield return copy[idx];
                copy.RemoveAt(idx);
            }
        }

        private static float Clamp(float v) => Math.Min(1f, Math.Max(0f, v));

        private static ulong HashSeed(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | hash[i];
            return value == 0 ? 0x9E3779B97F4A7C15UL : value;
        }

        public static string MakeNpcId(string fingerprint)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint ?? Guid.NewGuid().ToString("N")));
            return "npc_" + Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
        }
    }

    public static class NpcRolePolicy
    {
        private const string PoliceOccupation = "police officer";
        private const string PoliceBackground =
            "Works long shifts and has seen the city change. Believes the job is thankless but necessary, and is wary of people who act too friendly too fast.";
        private static readonly Dictionary<string, HashSet<string>> PreviouslyGeneratedJobs =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [NpcCategories.Civilian] = JobSet(
                    "warehouse worker", "delivery driver", "construction laborer", "barista", "retail clerk",
                    "security guard", "busboy", "line cook", "janitor", "unemployed", "student", "parking attendant"),
                [NpcCategories.Gang] = JobSet(
                    "gang lookout", "street dealer", "gang enforcer", "getaway driver", "gang recruiter"),
                [NpcCategories.Shopkeeper] = JobSet(
                    "convenience store owner", "liquor store owner", "pawn shop owner", "tattoo shop owner",
                    "restaurant owner", "barber"),
                [NpcCategories.Business] = JobSet(
                    "real estate agent", "car dealer", "nightclub manager", "contractor", "import exporter",
                    "property manager"),
                [NpcCategories.Employee] = JobSet(
                    "store employee", "hotel clerk", "valet", "waiter", "cashier", "stock clerk",
                    "call center worker"),
                [NpcCategories.Driver] = JobSet("taxi driver", "rideshare driver", "truck driver", "courier", "limo driver"),
                [NpcCategories.Passenger] = JobSet("commuter", "tourist", "student", "office worker"),
                [NpcCategories.Security] = JobSet("private security guard", "night watchman", "bodyguard", "mall security"),
                [NpcCategories.Paramedic] = JobSet("emergency medical technician", "hospital orderly"),
                [NpcCategories.Firefighter] = JobSet("fire inspector", "wildland firefighter"),
                [NpcCategories.Mechanic] = JobSet("auto mechanic", "tow truck driver", "body shop worker", "tire technician"),
                [NpcCategories.Homeless] = JobSet("panhandler", "transient laborer", "scrap collector"),
                [NpcCategories.Other] = JobSet("street performer", "artist", "musician", "unknown")
            };

        public static bool ReconcileObservedRole(NpcProfile profile, string observedCategory)
        {
            if (profile == null) return false;

            if (string.Equals(observedCategory, profile.Category, StringComparison.OrdinalIgnoreCase) &&
                PreviouslyGeneratedJobs.TryGetValue(observedCategory ?? "", out HashSet<string> oldJobs) &&
                oldJobs.Contains(profile.Occupation ?? ""))
            {
                profile.Occupation = CanonicalOccupation(observedCategory);
                profile.Background = RemoveGeneratedJobClaim(profile.Background);
                return true;
            }

            if (!string.Equals(observedCategory, NpcCategories.Police, StringComparison.OrdinalIgnoreCase))
                return false;

            string occupation = profile.Occupation ?? "";
            bool occupationMatches =
                occupation.IndexOf("police", StringComparison.OrdinalIgnoreCase) >= 0 ||
                occupation.IndexOf("officer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                occupation.IndexOf("detective", StringComparison.OrdinalIgnoreCase) >= 0 ||
                occupation.IndexOf("deputy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                occupation.IndexOf("sheriff", StringComparison.OrdinalIgnoreCase) >= 0;

            if (string.Equals(profile.Category, NpcCategories.Police, StringComparison.OrdinalIgnoreCase) &&
                occupationMatches)
                return false;

            profile.Category = NpcCategories.Police;
            profile.Occupation = PoliceOccupation;
            profile.Background = PoliceBackground;
            return true;
        }

        private static HashSet<string> JobSet(params string[] values)
        {
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        private static string CanonicalOccupation(string category)
        {
            switch (category)
            {
                case NpcCategories.Gang: return "gang member";
                case NpcCategories.Shopkeeper: return "shopkeeper";
                case NpcCategories.Business: return "business professional";
                case NpcCategories.Employee: return "employee";
                case NpcCategories.Driver: return "driver";
                case NpcCategories.Passenger: return "passenger";
                case NpcCategories.Security: return "security guard";
                case NpcCategories.Paramedic: return "paramedic";
                case NpcCategories.Firefighter: return "firefighter";
                case NpcCategories.Mechanic: return "mechanic";
                default: return "local resident";
            }
        }

        private static string RemoveGeneratedJobClaim(string background)
        {
            if (string.IsNullOrWhiteSpace(background)) return "Their exact occupation is not established.";

            int claim = background.LastIndexOf(" Works as a ", StringComparison.OrdinalIgnoreCase);
            if (claim >= 0)
                background = background.Substring(0, claim);
            return background.TrimEnd() + " Their exact occupation is not established.";
        }
    }

    /// <summary>
    /// Small deterministic PRNG (xorshift64*) so generated identities do not
    /// change when the .NET System.Random implementation changes.
    /// </summary>
    public sealed class StableRandom
    {
        private ulong _state;

        public StableRandom(ulong seed) { _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }

        public ulong NextULong()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 2685821657736338717UL;
        }

        public int Next(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextULong() % (ulong)maxExclusive);
        public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(Math.Max(1, maxExclusive - minInclusive));
        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);
        public float Float(float min, float max) => min + (float)NextDouble() * (max - min);
    }
}
