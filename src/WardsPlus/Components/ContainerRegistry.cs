using System;
using System.Collections.Generic;
using UnityEngine;
using WardsPlus.Util;
using Object = UnityEngine.Object;

namespace WardsPlus.Components
{
    /// <summary>
    /// Every loaded container a Gathering Ward could deposit into, on this client, and the deposit RPC on each
    /// one. Same model as <see cref="StationRegistry"/>: generic by component, so modded pieces built on the
    /// vanilla Container are included without any prefab names.
    ///
    /// Lifecycle:
    /// - Added from the container's own setup (Harmony postfix on Container.Awake), whenever the game creates it:
    ///   placing it, a zone loading, or loading a save. Placement previews (no network object) are skipped.
    /// - Removed when its GameObject is destroyed (removed, zone unloaded, logout), reported by a small
    ///   <see cref="ContainerTracker"/>; anything that still turns up Unity-null is dropped when the list is read.
    /// - Cleared on logout. The first read in each world also picks up containers loaded before the hooks
    ///   existed (hot reload).
    /// <see cref="Version"/> changes on every add/remove, so open panels only rescan when something changed.
    ///
    /// Never tracked (by component, so mods are covered too): carts and ships (anything with a Vagon or Ship
    /// above it), tombstones, and WardsPlus wards' own inventories. Whether a container is player-built,
    /// public, in range, or allowed by wards is checked live by the ward, because those can change.
    /// </summary>
    internal static class ContainerRegistry
    {
        internal sealed class Entry
        {
            public Container Container;
            public ZNetView View;
            public Piece Piece;
            public string Prefab;

            /// <summary>Localized display name: the piece's, falling back to the container's own.</summary>
            public string DisplayName
            {
                get
                {
                    string token = Piece && !string.IsNullOrEmpty(Piece.m_name) ? Piece.m_name : Container.m_name;
                    return Localization.instance.Localize(token);
                }
            }
        }

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly HashSet<Container> s_tracked = new HashSet<Container>();
        /// <summary>
        /// One container per network object: the deposit RPC lives on the ZNetView and can only be registered
        /// once, and two inventories couldn't both save into one object's item list anyway.
        /// </summary>
        private static readonly HashSet<ZNetView> s_views = new HashSet<ZNetView>();
        private static ZNetScene s_seededFor;

        /// <summary>Changes whenever a container is added or removed.</summary>
        public static int Version { get; private set; }

        /// <summary>Live containers. Don't keep the list across frames.</summary>
        public static List<Entry> Entries
        {
            get
            {
                EnsureSeeded();
                for (int i = s_entries.Count - 1; i >= 0; i--)
                {
                    if (!s_entries[i].Container)
                        RemoveAt(i);
                }
                return s_entries;
            }
        }

        public static void Track(Container container)
        {
            // The container's own resolved view: with m_rootObjectOverride it sits on a parent.
            ZNetView view = container ? container.m_nview : null;
            if (!view || view.GetZDO() == null)
                return; // Placement ghost or prefab.
            if (s_tracked.Contains(container) || s_views.Contains(view))
                return;
            if (IsExcluded(container))
                return;

            s_tracked.Add(container);
            s_views.Add(view);

            Piece piece = view.GetComponent<Piece>();
            if (!piece)
                piece = container.GetComponentInParent<Piece>();

            var entry = new Entry
            {
                Container = container,
                View = view,
                Piece = piece,
                Prefab = Utils.GetPrefabName(view.gameObject),
            };
            s_entries.Add(entry);
            Version++;

            container.gameObject.AddComponent<ContainerTracker>().Container = container;
            view.Register<ZDOID, ZPackage>(WardRpcs.ContainerDeposit,
                (sender, ward, items) => ContainerDelivery.OnDeposit(entry, sender, ward, items));
        }

        /// <summary>Called by <see cref="ContainerTracker"/> when the container's GameObject is destroyed.</summary>
        public static void Untrack(Container container)
        {
            if (!s_tracked.Contains(container))
                return;
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_entries[i].Container, container))
                    RemoveAt(i);
            }
        }

        public static void Clear()
        {
            s_entries.Clear();
            s_tracked.Clear();
            s_views.Clear();
            s_seededFor = null;
            Version++;
        }

        /// <summary>Carts, ships, tombstones and our own wards: never deposit targets, so not tracked at all.</summary>
        private static bool IsExcluded(Container container) =>
            container.m_wagon
            || container.GetComponentInParent<Vagon>()
            || container.GetComponentInParent<Ship>()
            || container.GetComponentInParent<TombStone>()
            || container.GetComponentInParent<WardBase>();

        private static void RemoveAt(int index)
        {
            Entry entry = s_entries[index];
            s_tracked.Remove(entry.Container);
            s_views.Remove(entry.View);
            if (entry.View)
                entry.View.Unregister(WardRpcs.ContainerDeposit); // Free the name if only the component went away.
            s_entries[index] = s_entries[s_entries.Count - 1];
            s_entries.RemoveAt(s_entries.Count - 1);
            Version++;
        }

        private static void EnsureSeeded()
        {
            ZNetScene scene = ZNetScene.instance;
            if (!scene || s_seededFor == scene)
                return;
            s_seededFor = scene;
            foreach (Container container in Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
                Track(container);
        }
    }

    /// <summary>Sits on a tracked container's GameObject and tells the registry when the game destroys it.</summary>
    internal sealed class ContainerTracker : MonoBehaviour
    {
        public Container Container;

        private void OnDestroy() => ContainerRegistry.Untrack(Container);
    }

    /// <summary>
    /// Moves items between a Gathering Ward and a container on the machine that owns the container, the only
    /// one allowed to change and save it. Items travel as an inventory package (vanilla's own item format), so
    /// durability, quality, crafter and modded custom data all survive. The container's owner adds only onto
    /// items it already holds ("place stacks"), measures what actually went in (so mods that refuse items are
    /// respected), and sends the rest back to the ward.
    /// </summary>
    internal static class ContainerDelivery
    {
        private const string TransferName = "WP_Transfer";

        /// <summary>
        /// Packs items in vanilla's inventory format, one single-slot inventory per stack (vanilla's loader puts
        /// each item at its saved slot and refuses a second, different item on the same slot). The items
        /// themselves aren't changed.
        /// </summary>
        public static ZPackage Pack(IEnumerable<ItemDrop.ItemData> items)
        {
            var stacks = new List<ZPackage>();
            foreach (ItemDrop.ItemData item in items)
            {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_gridPos = new Vector2i(0, 0);
                copy.m_equipped = false;
                var single = new Inventory(TransferName, null, 1, 1);
                single.m_inventory.Add(copy);
                var stack = new ZPackage();
                single.Save(stack);
                stacks.Add(stack);
            }

            var pkg = new ZPackage();
            pkg.Write(stacks.Count);
            foreach (ZPackage stack in stacks)
                pkg.Write(stack);
            return pkg;
        }

        /// <summary>
        /// Unpacks items packed by <see cref="Pack"/>. A stack whose item no longer exists in this game (a mod
        /// was removed) comes back empty from vanilla's loader and is skipped.
        /// </summary>
        public static List<ItemDrop.ItemData> Unpack(ZPackage pkg)
        {
            var items = new List<ItemDrop.ItemData>();
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++)
            {
                var single = new Inventory(TransferName, null, 1, 1);
                single.Load(pkg.ReadPackage());
                items.AddRange(single.GetAllItems());
            }
            return items;
        }

        /// <summary>
        /// How many of <paramref name="item"/> the container looks able to take, from its synced inventory:
        /// none unless it already holds that item (matching stacks only). An estimate; the owner re-checks.
        /// </summary>
        public static int EstimateRoom(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || !inventory.ContainsItemByName(item.m_shared.m_name))
                return 0;
            int maxStack = Mathf.Max(1, item.m_shared.m_maxStackSize);
            int stackRoom = maxStack > 1 ? inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) : 0;
            return stackRoom + inventory.GetEmptySlots() * maxStack;
        }

        public static void OnDeposit(ContainerRegistry.Entry entry, long sender, ZDOID ward, ZPackage pkg)
        {
            List<ItemDrop.ItemData> items;
            try
            {
                items = Unpack(pkg);
            }
            catch (Exception e)
            {
                // Unreadable means nothing to refund either (only a version mismatch could cause it).
                Plugin.Log.LogWarning($"Gathering: couldn't read a deposit for {entry.Prefab}, its items are lost: {e.Message}");
                return;
            }

            var rest = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData item in items)
            {
                int added = 0;
                try
                {
                    added = Apply(entry, item);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Gathering: depositing {item.m_stack} {item.m_shared.m_name} into {entry.Prefab} failed, returning it. {e.Message}");
                }
                int left = item.m_stack - added;
                if (left > 0)
                {
                    ItemDrop.ItemData back = item.Clone();
                    back.m_stack = left;
                    rest.Add(back);
                }
            }
            Refund(sender, ward, rest);
        }

        /// <summary>Owner side: adds one item onto matching stacks/slots and returns how many really went in.</summary>
        private static int Apply(ContainerRegistry.Entry entry, ItemDrop.ItemData item)
        {
            Container container = entry.Container;
            ZNetView view = entry.View;
            if (!container || !view || !view.IsValid() || !view.IsOwner())
                return 0;

            Inventory inventory = container.GetInventory();
            if (inventory == null || !inventory.ContainsItemByName(item.m_shared.m_name))
                return 0;

            string name = item.m_shared.m_name;
            int before = inventory.CountItems(name, -1, false);
            inventory.AddItem(item.Clone()); // Container saves itself on change (it's the owner here).
            int added = inventory.CountItems(name, -1, false) - before;
            return Mathf.Clamp(added, 0, item.m_stack);
        }

        /// <summary>Sends leftovers back to the ward on the machine that sent them (it forwards to the ward's current owner).</summary>
        private static void Refund(long sender, ZDOID ward, List<ItemDrop.ItemData> rest)
        {
            if (rest.Count == 0)
                return;
            if (ZRoutedRpc.instance == null || ward.IsNone())
            {
                Plugin.Log.LogWarning($"Gathering: couldn't return {rest.Count} item stack(s) to their ward.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ward, WardRpcs.DepositRefund, Pack(rest));
        }
    }
}
