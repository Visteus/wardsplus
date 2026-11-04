using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WardsPlus.Components;

namespace WardsPlus.Patches
{
    /// <summary>
    /// Defender Ward hooks:
    /// - build the trophy → creature list when a world finishes loading, drop it on logout;
    /// - mark summoned creatures on every client, and stop their drops, breeding and follow commands;
    /// - only let trophies that can summon something into a Defender Ward's slot.
    /// </summary>
    [HarmonyPatch]
    internal static class DefenderPatches
    {
        // ---------------------------------------------------------------- trophy map lifecycle

        /// <summary>Game.Start runs once the world's prefabs (vanilla and every mod's) are registered.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        private static void GameStart() => TrophyMap.EnsureBuilt();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
        private static void ZNetSceneDestroyed() => TrophyMap.Clear();

        // ---------------------------------------------------------------- summons

        /// <summary>Recognises summons on every client and after a reload, from the mark in their ZDO.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), nameof(Character.Awake))]
        private static void CharacterAwake(Character __instance) => DefenderSummon.AttachIfSummon(__instance);

        /// <summary>
        /// No drops from summons. Patched here rather than with CharacterDrop.SetDropsEnabled, because a ragdoll
        /// saves its own loot list from this method and drops it later regardless of that flag.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static bool GenerateDropList(CharacterDrop __instance, ref List<KeyValuePair<GameObject, int>> __result)
        {
            if (!DefenderSummon.IsSummon(__instance))
                return true;
            __result = new List<KeyValuePair<GameObject, int>>();
            return false;
        }

        /// <summary>
        /// A tamed creature's hits raise its owner's skill through its Tameable. Creatures that can't normally be
        /// tamed (trolls, abominations...) have none, and vanilla logs a warning on every hit. Summons skip it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character), nameof(Character.RaiseSkill))]
        private static bool RaiseSkill(Character __instance) =>
            !__instance.IsTamed() || !DefenderSummon.IsSummon(__instance);

        /// <summary>Summons don't breed.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Procreation), nameof(Procreation.Procreate))]
        private static bool Procreate(Procreation __instance) => !DefenderSummon.IsSummon(__instance);

        /// <summary>Summons can't be commanded to follow (or renamed), so they stay with their ward.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact))]
        private static bool TameableInteract(Tameable __instance, ref bool __result)
        {
            if (!DefenderSummon.IsSummon(__instance))
                return true;
            __result = false;
            return false;
        }

        // ---------------------------------------------------------------- trophy slot

        private static bool Rejects(Inventory inventory, ItemDrop.ItemData item) =>
            item != null && DefenderWard.IsDefenderInventory(inventory) && !TrophyMap.IsUsable(item);

        private static void TellPlayer()
        {
            if (Player.m_localPlayer && InventoryGui.IsVisible())
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$wp_defender_trophy_only");
        }

        /// <summary>
        /// Drag and drop. Checked before vanilla moves anything: when the slot is taken, vanilla swaps by
        /// removing the dragged item first, so refusing it later (in AddItem) would lose an item. This covers
        /// both directions: a non-trophy dropped onto the ward's slot, and the ward's trophy dropped onto a
        /// slot whose item would be swapped into the ward.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
        private static bool DropItem(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
        {
            Inventory target = __instance.m_inventory;
            ItemDrop.ItemData swapped = target?.GetItemAt(pos.x, pos.y);
            if (!Rejects(target, item) && (swapped == null || swapped == item || !Rejects(fromInventory, swapped)))
                return true;
            TellPlayer();
            __result = false;
            return false;
        }

        // Every other way in (shift-click, stack, other mods). A refused add here leaves the item where it was.

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(ItemDrop.ItemData))]
        private static bool AddItem(Inventory __instance, ItemDrop.ItemData item, ref bool __result) =>
            Allow(__instance, item, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(ItemDrop.ItemData), typeof(Vector2i))]
        private static bool AddItemAt(Inventory __instance, ItemDrop.ItemData item, ref bool __result) =>
            Allow(__instance, item, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem),
            typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool))]
        private static bool AddItemAmountAt(Inventory __instance, ItemDrop.ItemData item, ref bool __result) =>
            Allow(__instance, item, ref __result);

        private static bool Allow(Inventory inventory, ItemDrop.ItemData item, ref bool result)
        {
            if (!Rejects(inventory, item))
                return true;
            TellPlayer();
            result = false;
            return false;
        }
    }
}
