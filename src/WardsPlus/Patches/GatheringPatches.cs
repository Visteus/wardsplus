using HarmonyLib;
using WardsPlus.Components;
using WardsPlus.Util;

namespace WardsPlus.Patches
{
    /// <summary>
    /// Gathering Ward hooks:
    /// - keep <see cref="ContainerRegistry"/> in step with the containers the game creates (and clear it on logout);
    /// - wake wards when an item drop appears in their ring;
    /// - remember, for every player, which items a player dropped on purpose.
    /// </summary>
    [HarmonyPatch]
    internal static class GatheringPatches
    {
        /// <summary>Runs right after the container resolves its network object and inventory.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
        private static void ContainerAwake(Container __instance) => ContainerRegistry.Track(__instance);

        /// <summary>Logout: forget this world's containers.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
        private static void ZNetSceneDestroyed() => ContainerRegistry.Clear();

        /// <summary>Every drop that appears (dropped, smelter output, loot, zone load) notifies wards in range.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Start))]
        private static void ItemDropStart(ItemDrop __instance) => GatheringWard.OnItemSpawned(__instance);

        /// <summary>
        /// Vanilla marks a player-dropped item as "don't auto-pickup", but only on the dropping player's machine
        /// and not in the save. The dropping player just created the item, so owns it and can mark its ZDO,
        /// which every Gathering Ward then sees.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.OnPlayerDrop))]
        private static void OnPlayerDrop(ItemDrop __instance)
        {
            ZNetView view = __instance ? __instance.m_nview : null;
            if (view && view.IsValid() && view.IsOwner())
                view.GetZDO().Set(ZdoKeys.PlayerDropped, true);
        }
    }
}
