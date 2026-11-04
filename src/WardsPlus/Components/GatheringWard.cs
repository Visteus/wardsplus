using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using WardsPlus.Config;
using WardsPlus.UI;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// Picks up loose items in its range into its own inventory, and moves them on into nearby containers that
    /// already hold the same item.
    /// - Event first: a new drop inside the ring wakes the ward after Pickup Delay (see the ItemDrop.Start patch);
    ///   a slow Scan Interval pass catches anything else (e.g. items already lying around when it loads). No
    ///   physics: it walks vanilla's own list of loaded item drops.
    /// - Owner-safe: an item is only taken by the machine that owns it, which is vanilla's own pickup rule, so
    ///   overlapping wards or players can't duplicate items. If it doesn't own one yet it asks for it and comes
    ///   back a second later. A stack that only partly fits leaves the rest on the ground.
    /// - Never takes food placed on tables, items stuck in tar, eggs, fish, configured Ignored Items, or (by
    ///   default) items a player dropped.
    /// - Deposits (second, separately colored ring): only into public, player-built containers that already hold
    ///   the item, that the ward's protection rules allow, and whose type isn't turned off in the panel. The
    ///   container's owner applies it and sends back what didn't fit (<see cref="ContainerDelivery"/>).
    /// Range is measured horizontally, matching the rings.
    /// </summary>
    public class GatheringWard : WardBase
    {
        public override WardType Type => WardType.Gathering;

        protected override float IdleInterval => PluginConfig.GatheringScanInterval.Value;

        /// <summary>Next try after asking for an item's ownership (vanilla hands it over within a round trip).</summary>
        private const float OwnershipRetryDelay = 1f;
        private const float InventoryChangedTickDelay = 1f;
        private const int PanelListMaxRows = 7;

        private static readonly List<GatheringWard> s_loaded = new List<GatheringWard>();

        /// <summary>Container prefab types this ward won't deposit into.</summary>
        private readonly PrefabDenyList m_denyList =
            new PrefabDenyList(ZdoKeys.DepositDenied, ZdoKeys.DepositDeniedInit, WardRpcs.SetDepositDenied);

        private readonly List<ItemDrop> m_candidates = new List<ItemDrop>();
        private readonly List<KeyValuePair<ContainerRegistry.Entry, float>> m_containers =
            new List<KeyValuePair<ContainerRegistry.Entry, float>>();
        /// <summary>Set while putting refunded items back, so that doesn't immediately trigger another deposit try.</summary>
        private bool m_returningItems;

        private static readonly HashSet<string> s_ignoredItems = new HashSet<string>();
        private static string s_ignoredItemsSource;

        // Panel state while this ward's panel is open (controls are destroyed when it closes).
        private Text m_depositNote;
        private TypeToggleList m_containerList;
        private int m_listVersion = -1;
        private float m_listRange = -1f;
        private string m_listDenied;

        // ---------------------------------------------------------------- lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (!IsValid)
                return;
            s_loaded.Add(this);
            // A container type turned back on: deposit soon.
            m_denyList.Register(m_nview, () => RequestTickSoon(InventoryChangedTickDelay));
            m_nview.Register<ZPackage>(WardRpcs.DepositRefund, RPC_DepositRefund);
        }

        private void Start()
        {
            // The container lives on a child whose Awake may run after ours, so hook it up here.
            Inventory inventory = GetInventory();
            if (inventory != null)
                inventory.m_onChanged += OnInventoryChanged;
        }

        protected override void OnDestroy()
        {
            s_loaded.Remove(this);
            base.OnDestroy();
        }

        protected override void InitDefaults(ZDO zdo)
        {
            base.InitDefaults(zdo);
            if (!zdo.GetFloat(ZdoKeys.DepositRange, out _))
                zdo.Set(ZdoKeys.DepositRange, ClampSetting(ZdoKeys.DepositRange, PluginConfig.GatheringDefaultDepositRange.Value));
            m_denyList.InitDefaults(zdo, PluginConfig.GatheringDefaultDeniedContainers.Value);
        }

        protected override float ClampSetting(int key, float value) =>
            key == ZdoKeys.DepositRange
                ? Mathf.Clamp(value, 0f, Mathf.Max(0f, PluginConfig.GatheringMaxDepositRange.Value))
                : base.ClampSetting(key, value);

        private Inventory GetInventory() => m_container ? m_container.GetInventory() : null;

        /// <summary>Deposit range (m); 0 means the ward doesn't deposit.</summary>
        public float GetDepositRange() => GetFloatSetting(ZdoKeys.DepositRange, PluginConfig.GatheringDefaultDepositRange.Value);

        protected override float GetSecondaryRange() => GetDepositRange();

        /// <summary>
        /// Items taken out (so there's room again) or put in by a player (to deposit): act soon. Our own
        /// pickups and deposits happen during a tick, where this is ignored.
        /// </summary>
        private void OnInventoryChanged()
        {
            if (!m_returningItems && IsValid && m_nview.IsOwner() && IsEnabled())
                RequestTickSoon(InventoryChangedTickDelay);
        }

        /// <summary>From the ItemDrop.Start patch: a drop appeared; wake any owned ward whose ring holds it.</summary>
        public static void OnItemSpawned(ItemDrop drop)
        {
            if (!drop || s_loaded.Count == 0)
                return;
            Vector3 position = drop.transform.position;
            float delay = Mathf.Max(0f, PluginConfig.GatheringPickupDelay.Value) + 0.25f;
            foreach (GatheringWard ward in s_loaded)
            {
                if (ward && ward.IsValid && ward.m_nview.IsOwner() && ward.IsEnabled() && ward.InPickupRange(position))
                    ward.RequestTickSoon(delay);
            }
        }

        private bool InPickupRange(Vector3 position)
        {
            Vector3 offset = position - transform.position;
            offset.y = 0f;
            float range = GetRange();
            return offset.sqrMagnitude <= range * range;
        }

        // ---------------------------------------------------------------- simulation (owner only)

        protected override float OnTick()
        {
            float delay = NextIdleDelay(IdleInterval);
            Inventory inventory = GetInventory();
            if (inventory == null)
                return delay;

            // Items still settling or waiting for ownership: come back for them, not after a full scan interval.
            float retry = PickUpItems(inventory);
            DepositItems(inventory);
            return retry >= 0f ? Mathf.Min(delay, Mathf.Max(0.25f, retry)) : delay;
        }

        /// <summary>Takes what it can. Returns seconds until an item it skipped for now can be tried again, or -1.</summary>
        private float PickUpItems(Inventory inventory)
        {
            float pickupDelay = Mathf.Max(0f, PluginConfig.GatheringPickupDelay.Value);
            float soonest = -1f;

            // Copy first: taking an item destroys it, which changes vanilla's list.
            m_candidates.Clear();
            m_candidates.AddRange(ItemDrop.s_instances);
            foreach (ItemDrop drop in m_candidates)
            {
                if (!drop || !drop.m_nview || !drop.m_nview.IsValid() || !InPickupRange(drop.transform.position))
                    continue;
                if (!CanGather(drop))
                    continue;

                double age = drop.GetTimeSinceSpawned();
                if (age < pickupDelay)
                {
                    soonest = Soonest(soonest, (float)(pickupDelay - age));
                    continue;
                }

                drop.Load(); // Current stack from the synced ZDO.
                if (!HasRoomFor(inventory, drop.m_itemData))
                    continue; // Full for this item: the inventory changing wakes the ward again.

                if (!drop.CanPickup(false))
                {
                    // Not ours to take yet: ask its owner (vanilla backs off on repeats) and come back.
                    drop.RequestOwn();
                    soonest = Soonest(soonest, OwnershipRetryDelay);
                    continue;
                }

                TakeItem(inventory, drop);
            }
            m_candidates.Clear();
            return soonest;
        }

        private static float Soonest(float current, float candidate) =>
            current < 0f ? candidate : Mathf.Min(current, candidate);

        /// <summary>Items this ward may pick up at all, before range, age and room.</summary>
        private static bool CanGather(ItemDrop drop)
        {
            // Food placed on tables (a piece now), and items stuck in tar (vanilla won't let players take them).
            if (drop.IsPiece() || drop.InTar())
                return false;
            // Eggs must stay put to hatch; fish swim (a live fish is an item drop too).
            if (drop.GetComponent<EggGrow>() || drop.GetComponent<Fish>())
                return false;
            // Items with no icon can't be shown in an inventory (vanilla's own pickup check for players).
            ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
            if (shared.m_icons == null || shared.m_icons.Length == 0)
                return false;
            if (PluginConfig.GatheringIgnorePlayerDropped.Value && drop.m_nview.GetZDO().GetBool(ZdoKeys.PlayerDropped))
                return false;
            return !IsIgnoredItem(Utils.GetPrefabName(drop.gameObject));
        }

        private static bool IsIgnoredItem(string prefab)
        {
            string source = PluginConfig.GatheringIgnoredItems.Value ?? "";
            if (source != s_ignoredItemsSource)
            {
                s_ignoredItemsSource = source;
                s_ignoredItems.Clear();
                s_ignoredItems.UnionWith(NameLists.Parse(source));
            }
            return s_ignoredItems.Count > 0 && s_ignoredItems.Contains(prefab);
        }

        /// <summary>Room for at least one of this item: a matching stack with space, or an empty slot.</summary>
        private static bool HasRoomFor(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory.HaveEmptySlot())
                return true;
            return item.m_shared.m_maxStackSize > 1 &&
                inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) > 0;
        }

        /// <summary>
        /// Owner of the item only. Same steps as vanilla's own pickup: add the item to the inventory (which fills
        /// existing stacks first and shrinks the item's stack as it goes), then remove the drop if all of it fit,
        /// or save the smaller stack back onto it if not.
        /// </summary>
        private static void TakeItem(Inventory inventory, ItemDrop drop)
        {
            if (inventory.AddItem(drop.m_itemData))
                ZNetScene.instance.Destroy(drop.gameObject);
            else
                drop.Save();
        }

        // ---------------------------------------------------------------- deposits

        /// <summary>
        /// Sends items to nearby containers that already hold them, nearest first: one RPC per container, with
        /// only as many as it looks able to take. The container's owner re-checks and refunds the rest.
        /// </summary>
        private void DepositItems(Inventory inventory)
        {
            if (inventory.NrOfItems() == 0 || GetDepositRange() <= 0f)
                return;

            CollectDepositTargets(m_containers, applyDenyList: true);
            if (m_containers.Count == 0)
                return;
            m_containers.Sort((a, b) => a.Value.CompareTo(b.Value));

            var items = new List<ItemDrop.ItemData>(inventory.GetAllItems());
            var batch = new List<ItemDrop.ItemData>();
            foreach (KeyValuePair<ContainerRegistry.Entry, float> target in m_containers)
            {
                Inventory destination = target.Key.Container.GetInventory();
                batch.Clear();
                foreach (ItemDrop.ItemData item in items)
                {
                    if (item.m_stack <= 0 || !inventory.GetAllItems().Contains(item))
                        continue; // Already sent on in full.
                    int amount = Mathf.Min(item.m_stack, ContainerDelivery.EstimateRoom(destination, item));
                    if (amount <= 0)
                        continue;

                    ItemDrop.ItemData part = item.Clone();
                    part.m_stack = amount;
                    inventory.RemoveItem(item, amount);
                    batch.Add(part);
                }
                if (batch.Count > 0)
                    Send(target.Key, batch);
            }
            m_containers.Clear();
        }

        private void Send(ContainerRegistry.Entry target, List<ItemDrop.ItemData> batch)
        {
            try
            {
                target.View.InvokeRPC(WardRpcs.ContainerDeposit, m_nview.GetZDO().m_uid, ContainerDelivery.Pack(batch));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Gathering: sending {batch.Count} item stack(s) to {target.Prefab} failed, keeping them. {e.Message}");
                ReturnItems(batch);
            }
        }

        /// <summary>A container sent back items it couldn't take (full, changed owner, or an error).</summary>
        private void RPC_DepositRefund(long sender, ZPackage pkg)
        {
            if (!IsValid)
                return;
            if (!m_nview.IsOwner())
            {
                // Ownership moved since the deposit: the new owner holds the inventory now.
                m_nview.InvokeRPC(WardRpcs.DepositRefund, pkg);
                return;
            }
            List<ItemDrop.ItemData> items;
            try
            {
                items = ContainerDelivery.Unpack(pkg);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Gathering: couldn't read items returned to the ward: {e.Message}");
                return;
            }
            ReturnItems(items);
        }

        /// <summary>Puts items back in the inventory; anything that no longer fits is dropped beside the ward.</summary>
        private void ReturnItems(List<ItemDrop.ItemData> items)
        {
            Inventory inventory = GetInventory();
            m_returningItems = true;
            try
            {
                foreach (ItemDrop.ItemData item in items)
                {
                    if (item.m_stack <= 0)
                        continue;
                    // AddItem fills what it can and leaves the rest in item.m_stack.
                    if (inventory != null && inventory.AddItem(item))
                        continue;
                    if (item.m_stack <= 0 || !item.m_dropPrefab)
                        continue;
                    ItemDrop.DropItem(item, item.m_stack, transform.position + Vector3.up, Quaternion.identity);
                    Plugin.Log.LogInfo($"Gathering: ward inventory full, dropped {item.m_stack} {item.m_shared.m_name} beside it.");
                }
            }
            finally
            {
                m_returningItems = false;
            }
        }

        /// <summary>
        /// Containers this ward may deposit into, with their horizontal distance squared: player-built, public,
        /// not a loot bag, in deposit range, and allowed by ward protection (see <see cref="WardsAllow"/>).
        /// The panel lists these without the deny list, so turned-off types can be turned back on.
        /// </summary>
        private void CollectDepositTargets(List<KeyValuePair<ContainerRegistry.Entry, float>> results, bool applyDenyList)
        {
            results.Clear();
            float range = GetDepositRange();
            if (range <= 0f)
                return;
            Vector3 center = transform.position;
            float maxSqr = range * range;

            foreach (ContainerRegistry.Entry entry in ContainerRegistry.Entries)
            {
                Container container = entry.Container;
                Vector3 offset = container.transform.position - center;
                offset.y = 0f;
                float sqr = offset.sqrMagnitude;
                if (sqr > maxSqr)
                    continue;

                if (!entry.View || !entry.View.IsValid() || !entry.Piece || !entry.Piece.IsPlacedByPlayer())
                    continue;
                if (container.m_privacy != Container.PrivacySetting.Public || container.m_autoDestroyEmpty)
                    continue;
                if (applyDenyList && m_denyList.IsDenied(entry.Prefab))
                    continue;
                if (!WardsAllow(center, container.transform.position))
                    continue;

                results.Add(new KeyValuePair<ContainerRegistry.Entry, float>(entry, sqr));
            }
        }

        private static readonly HashSet<long> s_containerWardOwners = new HashSet<long>();

        /// <summary>
        /// Vanilla ward protection, as a player would meet it: a container no enabled player ward covers is open;
        /// one that is covered is only usable when the Gathering Ward stands in a ward owned by the same player
        /// (one ward covering both, or two wards placed by the same player). Modded wards built on PrivateArea
        /// count; dvergr and other location wards don't.
        /// </summary>
        private static bool WardsAllow(Vector3 ward, Vector3 container)
        {
            s_containerWardOwners.Clear();
            foreach (PrivateArea area in PrivateArea.m_allAreas)
            {
                if (IsPlayerWard(area, out long owner) && area.IsInside(container, 0f))
                    s_containerWardOwners.Add(owner);
            }
            if (s_containerWardOwners.Count == 0)
                return true;

            foreach (PrivateArea area in PrivateArea.m_allAreas)
            {
                if (IsPlayerWard(area, out long owner) && s_containerWardOwners.Contains(owner) && area.IsInside(ward, 0f))
                    return true;
            }
            return false;
        }

        /// <summary>An enabled ward placed by a player (the same "enabled" vanilla's access check uses).</summary>
        private static bool IsPlayerWard(PrivateArea area, out long owner)
        {
            owner = 0L;
            if (!area || area.m_ownerFaction != Character.Faction.Players || !area.m_piece || !area.IsEnabled())
                return false;
            owner = area.m_piece.GetCreator();
            return owner != 0L;
        }

        // ---------------------------------------------------------------- hover + panel

        protected override void AppendHoverStatus(StringBuilder sb)
        {
            Inventory inventory = GetInventory();
            if (inventory != null && !inventory.HaveEmptySlot())
                sb.Append("\n$wp_gathering_full");
        }

        public override void PopulatePanel(RectTransform extras)
        {
            Localization loc = Localization.instance;
            float maxDeposit = Mathf.Max(0f, PluginConfig.GatheringMaxDepositRange.Value);
            WardPanel.AddSlider(extras, "$wp_gathering_deposit_range", ZdoKeys.DepositRange, 0f, maxDeposit,
                w => ((GatheringWard)w).GetDepositRange(),
                v => v <= 0f ? loc.Localize("$wp_gathering_deposit_off") : $"{v:0}m");

            m_depositNote = WardPanel.AddNote(extras, loc.Localize("$wp_gathering_deposit_to"));
            m_containerList = new TypeToggleList(extras, m_denyList, "$wp_gathering_deposit_none", PanelListMaxRows);
            m_listVersion = -1;
            RefreshPanel();
        }

        /// <summary>
        /// The "Deposit to:" list is hidden while depositing is off, and otherwise rebuilt only when something
        /// that shapes it changed: a container added or removed anywhere, the deposit range, or the deny list.
        /// </summary>
        public override void RefreshPanel()
        {
            if (m_containerList == null || !m_containerList.IsAlive)
                return;

            bool depositing = GetDepositRange() > 0f;
            if (m_depositNote && m_depositNote.gameObject.activeSelf != depositing)
                m_depositNote.gameObject.SetActive(depositing);
            m_containerList.SetVisible(depositing);
            if (!depositing)
                return;

            if (ContainerRegistry.Version != m_listVersion
                || !Mathf.Approximately(GetDepositRange(), m_listRange)
                || m_denyList.Source != m_listDenied)
            {
                m_listVersion = ContainerRegistry.Version;
                m_listRange = GetDepositRange();
                m_listDenied = m_denyList.Source;

                var instances = new List<KeyValuePair<string, string>>();
                CollectDepositTargets(m_containers, applyDenyList: false);
                foreach (KeyValuePair<ContainerRegistry.Entry, float> target in m_containers)
                    instances.Add(new KeyValuePair<string, string>(target.Key.Prefab, target.Key.DisplayName));
                m_containers.Clear();
                m_containerList.Rebuild(instances);
            }
            m_containerList.SyncToggles();
        }
    }
}
