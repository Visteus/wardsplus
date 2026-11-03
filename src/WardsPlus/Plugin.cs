using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;
using WardsPlus.Config;
using WardsPlus.Pieces;
using WardsPlus.Util;

namespace WardsPlus
{
    // ModInfo is generated at build time from the .csproj (see Directory.Build.targets).
    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    [BepInDependency(Jotunn.Main.ModGuid, BepInDependency.DependencyFlags.HardDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        private readonly Harmony _harmony = new Harmony(ModInfo.Guid);

        // Awake runs once, when BepInEx's chainloader loads the plugin (before the main menu).
        private void Awake()
        {
            Instance = this;
            Log = Logger;

            PluginConfig.Bind(Config);
            Translations.Load();
            WardPieces.Init();

            _harmony.PatchAll();
        }

        // Lets hot-reload tools (e.g. ScriptEngine) cleanly unload the mod.
        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
