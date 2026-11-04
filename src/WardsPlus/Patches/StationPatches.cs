using HarmonyLib;
using WardsPlus.Components;

namespace WardsPlus.Patches
{
    /// <summary>
    /// Keeps <see cref="StationRegistry"/> in step with the stations the game creates. Each hook runs right
    /// after the component's own setup, once its network object is resolved.
    /// </summary>
    [HarmonyPatch]
    internal static class StationPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
        private static void FireplaceAwake(Fireplace __instance) => StationRegistry.Track(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
        private static void SmelterAwake(Smelter __instance) => StationRegistry.Track(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.Awake))]
        private static void CookingStationAwake(CookingStation __instance) => StationRegistry.Track(__instance);

        // ShieldGenerator sets itself up in Start, not Awake.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ShieldGenerator), nameof(ShieldGenerator.Start))]
        private static void ShieldGeneratorStart(ShieldGenerator __instance) => StationRegistry.Track(__instance);

        /// <summary>Logout: forget this world's stations.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
        private static void ZNetSceneDestroyed() => StationRegistry.Clear();
    }
}
