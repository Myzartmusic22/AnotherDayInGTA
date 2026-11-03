using System;
using System.Collections.Generic;
using System.Linq;
using AnotherDay.Shared;
using GTA;
using GTA.Math;
using GTA.Native;

namespace AnotherDay.Plugin.Game
{
    internal sealed class PedCollector
    {
        private readonly Dictionary<int, string> _fingerprints = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _fingerprintModels = new Dictionary<int, int>();
        private readonly WorldSensor _sensor = new WorldSensor();

        public PedSnapshotRequest Collect(Player player, PluginConfig config)
        {
            Ped character = player.Character;
            var request = new PedSnapshotRequest
            {
                PlayerPosition = ToVec(character.Position),
                PlayerHeading = character.Heading,
                PlayerHandle = character.Handle,
                PlayerArmed = IsArmed(character),
                PlayerAiming = player.IsAiming,
                PlayerWantedLevel = player.WantedLevel,
                PlayerVehicleModel = character.CurrentVehicle != null
                    ? character.CurrentVehicle.Model.Hash.ToString()
                    : null,
                Zone = SafeZone(character.Position),
                Weather = SafeWeather(),
                ClockHour = Function.Call<int>(Hash.GET_CLOCK_HOURS),
                ClockMinute = Function.Call<int>(Hash.GET_CLOCK_MINUTES)
            };

            Ped[] nearby = World.GetNearbyPeds(character, config.ConversationRangeMeters + 25f);
            if (nearby == null) nearby = new Ped[0];

            var ordered = nearby
                .Where(p => p != null && p.Exists() && p.Handle != character.Handle)
                .OrderBy(p => p.Position.DistanceTo(character.Position))
                .Take(config.MaxNearbyPeds)
                .ToList();

            request.Events.AddRange(_sensor.Observe(player, nearby, request.Zone, request.Weather, request.ClockHour));

            foreach (Ped ped in ordered)
            {
                if (ped.IsDead || !ped.IsHuman) continue;
                string fingerprint = Fingerprint(ped);
                bool inVehicle = ped.IsInVehicle();
                string category = CategoryOf(ped);

                var observation = new PedObservation
                {
                    Handle = ped.Handle,
                    Fingerprint = fingerprint,
                    ModelHash = ped.Model.Hash,
                    ModelName = "model_" + ped.Model.Hash,
                    IsMale = PedGender.IsMale(ped),
                    CategoryHint = category,
                    Position = ToVec(ped.Position),
                    Heading = ped.Heading,
                    Health = ped.Health,
                    MaxHealth = ped.MaxHealth,
                    Armor = ped.Armor,
                    IsDead = false,
                    IsInVehicle = inVehicle,
                    VehicleHandle = ped.CurrentVehicle?.Handle ?? 0,
                    VehicleModel = ped.CurrentVehicle != null ? ped.CurrentVehicle.Model.Hash.ToString() : null,
                    VehicleSeat = inVehicle ? (IsDriver(ped) ? -1 : 0) : 0,
                    WeaponHash = WeaponHash(ped),
                    IsArmed = IsArmed(ped),
                    IsInCombat = Function.Call<bool>(Hash.IS_PED_IN_COMBAT, ped, 0),
                    IsAiming = false,
                    IsFleeing = Function.Call<bool>(Hash.IS_PED_FLEEING, ped),
                    Speed = ped.Speed,
                    RelationshipGroup = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, ped).ToString("X8"),
                    IsOnScreen = ped.IsOnScreen,
                    DistanceToPlayer = ped.Position.DistanceTo(character.Position)
                };
                request.Peds.Add(observation);
            }

            return request;
        }

        private string Fingerprint(Ped ped)
        {
            int model = ped.Model.Hash;
            if (_fingerprints.TryGetValue(ped.Handle, out string cached) &&
                _fingerprintModels.TryGetValue(ped.Handle, out int cachedModel) && cachedModel == model)
                return cached;

            var parts = new List<string> { model.ToString("X8") };
            for (int component = 0; component < 12; component++)
            {
                int drawable = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, component);
                int texture = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped, component);
                parts.Add(drawable + "." + texture);
            }
            for (int prop = 0; prop < 8; prop++)
            {
                int index = Function.Call<int>(Hash.GET_PED_PROP_INDEX, ped, prop);
                int texture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, ped, prop);
                parts.Add("p" + prop + ":" + index + "." + texture);
            }

            string fingerprint = string.Join("_", parts);
            _fingerprints[ped.Handle] = fingerprint;
            _fingerprintModels[ped.Handle] = model;
            return fingerprint;
        }

        /// <summary>
        /// The category the snapshot reports, reused by the target selector so
        /// the identity in the HUD and the description used before an identity
        /// exists can never disagree.
        /// </summary>
        internal static string CategoryOf(Ped ped)
        {
            if (ped == null || !ped.Exists()) return NpcCategories.Other;
            try
            {
                bool inVehicle = ped.IsInVehicle();
                int pedType = Function.Call<int>(Hash.GET_PED_TYPE, ped);
                if (pedType == 2) return NpcCategories.Police;
                if (pedType == 3) return NpcCategories.Gang;
                if (ped.Model.IsGangPed) return NpcCategories.Gang;
                if (inVehicle) return IsDriver(ped) ? NpcCategories.Driver : NpcCategories.Passenger;
                return NpcCategories.Civilian;
            }
            catch
            {
                return NpcCategories.Other;
            }
        }

        private static bool IsDriver(Ped ped)
        {
            Vehicle vehicle = ped.CurrentVehicle;
            if (vehicle == null) return false;
            return Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1) == ped.Handle;
        }

        private static bool IsArmed(Ped ped)
        {
            try { return Function.Call<bool>(Hash.IS_PED_ARMED, ped, 7); }
            catch { return false; }
        }

        private static int WeaponHash(Ped ped)
        {
            try { return (int)ped.Weapons.Current.Hash; }
            catch { return 0; }
        }

        internal static Vec3 ToVec(Vector3 v) => new Vec3(v.X, v.Y, v.Z);

        private static string SafeZone(Vector3 position)
        {
            try { return World.GetZoneLocalizedName(position); }
            catch { return "Los Santos"; }
        }

        private static string SafeWeather()
        {
            try { return World.Weather.ToString(); }
            catch { return "clear"; }
        }
    }

    /// <summary>
    /// Detects a conservative set of observable events. Cooldowns stop a single
    /// gunfight from flooding the service with duplicate facts.
    /// </summary>
    internal sealed class WorldSensor
    {
        private sealed class PedState
        {
            public int Health;
            public bool Dead;
            public bool InCombat;
        }

        private readonly Dictionary<int, PedState> _states = new Dictionary<int, PedState>();
        private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>();
        private string _lastZone;
        private string _lastWeather;
        private int _lastWanted;

        public List<WorldEventObservation> Observe(Player player, Ped[] nearby, string zone, string weather, int hour)
        {
            var events = new List<WorldEventObservation>();
            Ped character = player.Character;

            if (!string.IsNullOrEmpty(_lastZone) && !string.Equals(_lastZone, zone, StringComparison.OrdinalIgnoreCase))
                Add(events, WorldEventTypes.ZoneChange, character.Position, 40f, 0.4f, 0, "Moved into " + zone, zone, "zone");
            _lastZone = zone;

            if (!string.IsNullOrEmpty(_lastWeather) && !string.Equals(_lastWeather, weather, StringComparison.OrdinalIgnoreCase))
                Add(events, WorldEventTypes.WeatherChange, character.Position, 120f, 0.3f, 0, "Weather changed to " + weather, zone, "weather");
            _lastWeather = weather;

            if (player.WantedLevel > _lastWanted && player.WantedLevel > 0)
                Add(events, WorldEventTypes.PoliceActivity, character.Position, 60f, 0.8f, 0, "Police response escalated", zone, "police");
            _lastWanted = player.WantedLevel;

            if (player.IsAiming && player.IsTargetingAnything)
                Add(events, WorldEventTypes.PlayerAiming, character.Position, 25f, 0.9f, character.Handle, "The player is aiming a weapon", zone, "aim");

            if (nearby != null)
            {
                foreach (Ped ped in nearby)
                {
                    if (ped == null || !ped.Exists() || ped.Handle == character.Handle) continue;
                    PedState state;
                    if (!_states.TryGetValue(ped.Handle, out state))
                    {
                        state = new PedState { Health = ped.Health, Dead = ped.IsDead };
                        _states[ped.Handle] = state;
                    }

                    if (ped.IsDead && !state.Dead)
                        Add(events, WorldEventTypes.Death, ped.Position, 25f, 1f, ped.Handle, "Someone died nearby", zone, "death" + ped.Handle);
                    else if (!ped.IsDead && !state.Dead && state.Health - ped.Health >= 25)
                        Add(events, WorldEventTypes.Injury, ped.Position, 20f, 0.7f, ped.Handle, "Someone was injured nearby", zone, "injury" + ped.Handle);
                    state.Dead = ped.IsDead;
                    state.Health = ped.Health;

                    if (!ped.IsDead)
                    {
                        if (Function.Call<bool>(Hash.IS_PED_SHOOTING, ped))
                            Add(events, WorldEventTypes.Gunshot, ped.Position, 55f, 1f, ped.Handle, "Gunshots nearby", zone, "shot" + ped.Handle);

                        bool inCombat = Function.Call<bool>(Hash.IS_PED_IN_COMBAT, ped, 0);
                        if (inCombat && !state.InCombat)
                            Add(events, WorldEventTypes.Fight, ped.Position, 22f, 0.6f, ped.Handle, "A fight broke out nearby", zone, "fight" + ped.Handle);
                        state.InCombat = inCombat;
                    }
                }
            }

            try
            {
                if (Function.Call<bool>(Hash.IS_EXPLOSION_IN_SPHERE, -1, character.Position.X, character.Position.Y, character.Position.Z, 70f))
                    Add(events, WorldEventTypes.Explosion, character.Position, 80f, 1.5f, 0, "An explosion nearby", zone, "explosion");
            }
            catch { }

            try
            {
                Vehicle[] vehicles = World.GetNearbyVehicles(character, 45f);
                foreach (Vehicle vehicle in vehicles ?? new Vehicle[0])
                {
                    if (vehicle == null || !vehicle.Exists()) continue;
                    if (vehicle.IsSirenActive)
                        Add(events, WorldEventTypes.EmergencyVehicle, vehicle.Position, 40f, 0.7f, vehicle.Handle,
                            "An emergency vehicle with sirens passed", zone, "siren" + vehicle.Handle);
                    if (vehicle.HasCollided && vehicle.Speed < 3.5f)
                        Add(events, WorldEventTypes.Crash, vehicle.Position, 18f, 0.6f, vehicle.Handle,
                            "A vehicle collision nearby", zone, "crash" + vehicle.Handle);
                }
            }
            catch { }

            return events;
        }

        private void Add(List<WorldEventObservation> events, string type, Vector3 position, float radius,
            float magnitude, int sourceHandle, string description, string zone, string key)
        {
            if (events.Count >= 8) return;
            if (_cooldowns.TryGetValue(key, out DateTime last) && (DateTime.UtcNow - last).TotalSeconds < CooldownFor(type)) return;
            _cooldowns[key] = DateTime.UtcNow;
            events.Add(new WorldEventObservation
            {
                Type = type,
                Position = PedCollector.ToVec(position),
                Radius = radius,
                Magnitude = magnitude,
                SourceHandle = sourceHandle,
                Description = description,
                Zone = zone
            });
        }

        private static double CooldownFor(string type)
        {
            switch (type)
            {
                case WorldEventTypes.WeatherChange: return 120;
                case WorldEventTypes.ZoneChange: return 10;
                case WorldEventTypes.PoliceActivity: return 20;
                case WorldEventTypes.PlayerAiming: return 10;
                case WorldEventTypes.EmergencyVehicle: return 30;
                case WorldEventTypes.Crash: return 15;
                case WorldEventTypes.Gunshot: return 6;
                case WorldEventTypes.Fight: return 12;
                case WorldEventTypes.Explosion: return 8;
                default: return 8;
            }
        }
    }
}
