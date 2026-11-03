using HarmonyLib;

namespace WardsPlus.Patches
{
    // /// <summary>
    // /// Postfix: runs after Player.OnSpawned. Shows a message to the local player.
    // /// </summary>
    // [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    // internal static class PlayerOnSpawnedPatch
    // {
    //     // __instance is the Player the original method ran on.
    //     private static void Postfix(Player __instance)
    //     {
    //         // OnSpawned runs for every player object, including other players' characters in multiplayer.
    //         if (__instance != Player.m_localPlayer || !Plugin.ShowWelcomeMessage.Value)
    //             return;

    //         __instance.Message(MessageHud.MessageType.Center, Plugin.WelcomeText.Value);
    //     }
    // }

    // /// <summary>
    // /// Postfix with __result: changes the value the original method returned.
    // /// </summary>
    // [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
    // internal static class PlayerGetMaxCarryWeightPatch
    // {
    //     private static void Postfix(Player __instance, ref float __result)
    //     {
    //         if (__instance == Player.m_localPlayer)
    //             __result += Plugin.ExtraCarryWeight.Value;
    //     }
    // }
}
