using System;
using System.Collections.Generic;
using System.Reflection;

namespace WardsPlus.Components
{
    /// <summary>
    /// Optional support for KG's ItemDrawers mod, whose drawers aren't vanilla containers: each drawer holds one
    /// item type (prefab + quality) and a count in its own ZDO, and is filled through its own RPC. Uses the mod's
    /// published API entry point (the same one its ItemDrawers_API helper uses), looked up by name, so there's
    /// no hard dependency: without the mod everything here returns nothing.
    ///
    /// A drawer only takes items that exactly match what it already holds (same prefab and quality, no custom
    /// data), which is the Gathering Ward's "matching stacks only" rule. It has no capacity limit, so nothing
    /// is refunded, and an add that no longer matches is thrown away by the mod; so wards only deposit into
    /// drawers their own machine owns, where the check and the add happen together.
    /// </summary>
    internal static class ItemDrawersSupport
    {
        // From kg_ItemDrawers (API.ClientSideV2 / DrawerComponent).
        private const string ApiType = "API.ClientSideV2, kg_ItemDrawers";
        private const string AllDrawersMethod = "AllDrawers";
        private const string AddItemRpc = "AddItem_Request";
        private const string PrefabKey = "Prefab";
        private const string QualityKey = "Quality";
        private const string AmountKey = "Amount";

        private static bool s_checked;
        private static MethodInfo s_allDrawers;
        private static readonly List<ZNetView> s_none = new List<ZNetView>();

        public static bool Installed
        {
            get
            {
                if (!s_checked)
                {
                    s_checked = true;
                    try
                    {
                        s_allDrawers = Type.GetType(ApiType)?.GetMethod(AllDrawersMethod, BindingFlags.Static | BindingFlags.Public);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"ItemDrawers support unavailable: {e.Message}");
                    }
                    if (s_allDrawers != null)
                        Plugin.Log.LogInfo("ItemDrawers found: Gathering Wards can deposit into drawers.");
                }
                return s_allDrawers != null;
            }
        }

        /// <summary>Every loaded drawer's network object (valid ones only), or an empty list without the mod.</summary>
        public static List<ZNetView> Drawers()
        {
            if (!Installed)
                return s_none;
            try
            {
                return s_allDrawers.Invoke(null, null) as List<ZNetView> ?? s_none;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"ItemDrawers: couldn't list drawers, skipping them. {e.Message}");
                s_allDrawers = null; // Don't retry (and log) every tick.
                return s_none;
            }
        }

        /// <summary>The item a drawer holds (from its synced ZDO). False if it's empty/unassigned.</summary>
        public static bool TryGetContents(ZNetView drawer, out string prefab, out int quality)
        {
            ZDO zdo = drawer && drawer.IsValid() ? drawer.GetZDO() : null;
            prefab = zdo?.GetString(PrefabKey, "") ?? "";
            quality = Math.Max(1, zdo?.GetInt(QualityKey, 1) ?? 1);
            return prefab.Length > 0 && ObjectDB.instance && ObjectDB.instance.GetItemPrefab(prefab);
        }

        /// <summary>How many items the drawer holds (from its ZDO; current on the drawer's owner).</summary>
        public static int GetAmount(ZNetView drawer)
        {
            ZDO zdo = drawer && drawer.IsValid() ? drawer.GetZDO() : null;
            return zdo?.GetInt(AmountKey, 0) ?? 0;
        }

        /// <summary>Whether a drawer holding <paramref name="prefab"/>/<paramref name="quality"/> takes this item.</summary>
        public static bool Accepts(ItemDrop.ItemData item, string prefab, int quality) =>
            item.m_dropPrefab && item.m_dropPrefab.name == prefab && item.m_quality == quality &&
            (item.m_customData == null || item.m_customData.Count == 0);

        /// <summary>Adds items to the drawer on its owner (the mod's own RPC).</summary>
        public static void Deposit(ZNetView drawer, string prefab, int amount, int quality) =>
            drawer.InvokeRPC(AddItemRpc, prefab, amount, quality);
    }
}
