using HarmonyLib;
using WardsPlus.UI;

namespace WardsPlus.Patches
{
    [HarmonyPatch(typeof(InventoryGui))]
    internal static class InventoryGuiPatches
    {
        /// <summary>Shows/hides/refreshes the ward settings panel alongside the inventory screen.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InventoryGui.Update))]
        private static void UpdatePostfix(InventoryGui __instance)
        {
            WardPanel.Tick(__instance);
        }
    }
}
