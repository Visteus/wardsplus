using System.Collections.Generic;
using BepInEx.Configuration;
using Jotunn.Configs;
using UnityEngine;

namespace WardsPlus.Config
{
    /// <summary>Every ward type the mod adds.</summary>
    public enum WardType
    {
        Repair,
        Refuel,
        Spawnproof,
        Defender,
    }

    /// <summary>Config entries shared by every ward type (one section per ward).</summary>
    internal sealed class WardConfig
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> Recipe;
        public ConfigEntry<string> CraftingStation;
        public ConfigEntry<float> DefaultRange;
        public ConfigEntry<float> MinRange;
        public ConfigEntry<float> MaxRange;
        public ConfigEntry<Color> Color;

        /// <summary>Clamps a requested range to the server-synced min/max.</summary>
        public float ClampRange(float value)
        {
            float min = Mathf.Min(MinRange.Value, MaxRange.Value);
            float max = Mathf.Max(MinRange.Value, MaxRange.Value);
            return Mathf.Clamp(value, min, max);
        }

        /// <summary>
        /// Parses "Item:Amount,Item:Amount" into Jotunn requirement configs. Bad entries are logged and skipped.
        /// </summary>
        public RequirementConfig[] ParseRecipe()
        {
            var result = new List<RequirementConfig>();
            foreach (string raw in Recipe.Value.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                    continue;

                string[] parts = entry.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int amount) || amount <= 0)
                {
                    Plugin.Log.LogWarning($"[{Recipe.Definition.Section}] Ignoring invalid recipe entry '{entry}'. Expected Item:Amount.");
                    continue;
                }

                result.Add(new RequirementConfig(parts[0].Trim(), amount, 0, true));
            }
            return result.ToArray();
        }
    }

    /// <summary>
    /// All BepInEx config entries. Entries marked admin-only are synced from the server by Jotunn,
    /// so a dedicated server's values win over each client's local file.
    /// </summary>
    internal static class PluginConfig
    {
        private static readonly Dictionary<WardType, WardConfig> Wards = new Dictionary<WardType, WardConfig>();

        public static WardConfig For(WardType type) => Wards[type];

        /// <summary>Whether the vanilla ward's spherical glow orb is shown on active wards.</summary>
        public static ConfigEntry<bool> ShowGlowOrb;

        public static void Bind(ConfigFile cfg)
        {
            ShowGlowOrb = cfg.Bind("General", "Show Glow Orb", false,
                "Show the vanilla ward's spherical glow above active wards. When off, only the colored magic lines glow. Client-side. Requires a restart.");

            Wards[WardType.Repair] = BindWard(cfg, WardType.Repair,
                recipe: "RoundLog:10,Stone:10,GreydwarfEye:50", station: "piece_workbench",
                defaultRange: 20f, minRange: 5f, maxRange: 40f, color: new Color(0.25f, 1f, 0.35f));

            Wards[WardType.Refuel] = BindWard(cfg, WardType.Refuel,
                recipe: "FineWood:10,Stone:10,Coal:25,SurtlingCore:1", station: "piece_workbench",
                defaultRange: 20f, minRange: 5f, maxRange: 40f, color: new Color(1f, 0.5f, 0.1f));

            Wards[WardType.Spawnproof] = BindWard(cfg, WardType.Spawnproof,
                recipe: "ElderBark:10,Stone:50,SurtlingCore:1", station: "piece_workbench",
                defaultRange: 30f, minRange: 10f, maxRange: 60f, color: new Color(0.2f, 0.6f, 1f));

            Wards[WardType.Defender] = BindWard(cfg, WardType.Defender,
                recipe: "YggdrasilWood:10,BlackMarble:10,BlackCore:1,DragonTear:5", station: "forge",
                defaultRange: 20f, minRange: 5f, maxRange: 20f, color: new Color(0.7f, 0.3f, 1f));
        }

        private static WardConfig BindWard(ConfigFile cfg, WardType type, string recipe, string station,
            float defaultRange, float minRange, float maxRange, Color color)
        {
            string section = $"{type} Ward";
            var admin = new ConfigurationManagerAttributes { IsAdminOnly = true };
            var rangeLimits = new AcceptableValueRange<float>(1f, 200f);

            return new WardConfig
            {
                Enabled = cfg.Bind(section, "Enabled", true,
                    new ConfigDescription("Add this ward to the hammer. Requires a restart.", null,
                        new ConfigurationManagerAttributes { IsAdminOnly = true, Order = 10 })),
                Recipe = cfg.Bind(section, "Recipe", recipe,
                    new ConfigDescription("Build cost as Item:Amount pairs, comma separated (item prefab names). Requires a restart.", null,
                        new ConfigurationManagerAttributes { IsAdminOnly = true, Order = 9 })),
                CraftingStation = cfg.Bind(section, "Crafting Station", station,
                    new ConfigDescription("Prefab name of the station required nearby to build it (e.g. piece_workbench, forge, blackforge). Empty for none. Requires a restart.", null,
                        new ConfigurationManagerAttributes { IsAdminOnly = true, Order = 8 })),
                DefaultRange = cfg.Bind(section, "Default Range", defaultRange,
                    new ConfigDescription("Range (m) a newly placed ward starts with.", rangeLimits, admin)),
                MinRange = cfg.Bind(section, "Min Range", minRange,
                    new ConfigDescription("Smallest range (m) players can set in the ward's panel.", rangeLimits, admin)),
                MaxRange = cfg.Bind(section, "Max Range", maxRange,
                    new ConfigDescription("Largest range (m) players can set in the ward's panel.", rangeLimits, admin)),
                Color = cfg.Bind(section, "Glow Color", color,
                    "Color of the ward's magic lines, glow and icon. Client-side. Requires a restart."),
            };
        }
    }
}
