using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace WardsPlus
{
    // ModInfo is generated at build time from the .csproj (see Directory.Build.targets).
    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    // Declare dependencies on other plugins by their GUID. Hard = refuse to load without it.
    // [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.HardDependency)]
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

        }

        // Lets hot-reload tools (e.g. ScriptEngine) cleanly unload the mod.
        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }
    }
}
