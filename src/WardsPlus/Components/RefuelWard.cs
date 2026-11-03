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
    /// Keeps the fires, braziers, sconces, torches, smelters, ovens, shield generators and kilns in range
    /// supplied from its inventory.
    /// - Every Scan Interval (jittered), or ~1s after items are added, each allowed station in range (nearest
    ///   first) gets everything it has room for in one delivery: fuel first (fires, smelter/furnace/refinery,
    ///   oven, shield generator), then the input of "input stations" (the charcoal kiln's wood) from what's left.
    ///   Smelter ore is never loaded.
    /// - A delivery takes the items out in one go and sends one RPC; the station's owner clamps it to the
    ///   room it really has and sends back anything left over (see <see cref="StationDelivery"/>), so
    ///   overlapping wards can't overfill a station or lose items.
    /// - Each ward has its own deny list of prefab types, edited in its panel: every station type in range is
    ///   listed once ("Campfire ×5") with a checkbox, and the choice applies to every piece of that type.
    /// Stations come from <see cref="StationRegistry"/>; range is measured horizontally, matching the ring.
    /// </summary>
    public class RefuelWard : WardBase
    {
        public override WardType Type => WardType.Refuel;

        protected override float IdleInterval => PluginConfig.RefuelScanInterval.Value;

        private const float FuelAddedTickDelay = 1f;
        private const int PanelListMaxRows = 7;

        /// <summary>A station in range and how many items it can take right now (from the synced ZDO).</summary>
        private struct Target
        {
            public StationRegistry.Station Station;
            /// <summary>True: load the station's input (kiln wood). False: add fuel.</summary>
            public bool IsInput;
            public int Room;
            public float SqrDistance;
        }

        private readonly List<Target> m_targets = new List<Target>();
        private readonly HashSet<string> m_denied = new HashSet<string>();
        private string m_deniedSource;

        private static readonly HashSet<string> s_inputStations = new HashSet<string>();
        private static string s_inputStationsSource;

        // Panel state while this ward's panel is open (controls are destroyed when it closes).
        private RectTransform m_typeList;
        private readonly Dictionary<string, Toggle> m_typeToggles = new Dictionary<string, Toggle>();
        private string m_typeListSignature;
        // What the list was last built from; any change triggers a rescan.
        private int m_listVersion = -1;
        private float m_listRange = -1f;
        private string m_listDenied;
        private string m_listInputs;

        // ---------------------------------------------------------------- lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (!IsValid)
                return;
            m_nview.Register<string, bool>(WardRpcs.SetDenied, RPC_SetDenied);
            m_nview.Register<string, int>(WardRpcs.Refund, RPC_Refund);
        }

        private void Start()
        {
            // The container lives on a child whose Awake may run after ours, so hook it up here.
            Inventory inventory = GetInventory();
            if (inventory != null)
                inventory.m_onChanged += OnInventoryChanged;
        }

        protected override void InitDefaults(ZDO zdo)
        {
            base.InitDefaults(zdo);
            if (!zdo.GetBool(ZdoKeys.DeniedInit))
            {
                zdo.Set(ZdoKeys.Denied, JoinNames(ParseNames(PluginConfig.RefuelDefaultDenied.Value)));
                zdo.Set(ZdoKeys.DeniedInit, true);
            }
        }

        private Inventory GetInventory() => m_container ? m_container.GetInventory() : null;

        /// <summary>Fuel added: top up soon instead of waiting out the scan interval.</summary>
        private void OnInventoryChanged()
        {
            if (IsValid && m_nview.IsOwner() && IsEnabled())
                RequestTickSoon(FuelAddedTickDelay);
        }

        // ---------------------------------------------------------------- deny list (ZDO-backed)

        public bool IsDenied(string prefab)
        {
            string source = IsValid ? m_nview.GetZDO().GetString(ZdoKeys.Denied) : "";
            if (source != m_deniedSource)
            {
                m_deniedSource = source;
                m_denied.Clear();
                m_denied.UnionWith(ParseNames(source));
            }
            return m_denied.Contains(prefab);
        }

        /// <summary>Asks the owner to deny or allow one prefab type. Safe to call from any client.</summary>
        public void RequestSetDenied(string prefab, bool denied)
        {
            if (IsValid)
                m_nview.InvokeRPC(WardRpcs.SetDenied, prefab, denied);
        }

        // Per-type RPC rather than sending the whole list, so two players toggling different types at once
        // can't overwrite each other's change.
        private void RPC_SetDenied(long sender, string prefab, bool denied)
        {
            if (!m_nview.IsOwner() || string.IsNullOrEmpty(prefab))
                return;
            var names = new HashSet<string>(ParseNames(m_nview.GetZDO().GetString(ZdoKeys.Denied)));
            bool changed = denied ? names.Add(prefab) : names.Remove(prefab);
            if (!changed)
                return;
            m_nview.GetZDO().Set(ZdoKeys.Denied, JoinNames(names));
            if (!denied)
                RequestTickSoon(FuelAddedTickDelay); // A type was turned back on: fuel it soon.
        }

        private static IEnumerable<string> ParseNames(string source)
        {
            if (string.IsNullOrEmpty(source))
                yield break;
            foreach (string raw in source.Split(','))
            {
                string name = raw.Trim();
                if (name.Length > 0)
                    yield return name;
            }
        }

        private static string JoinNames(IEnumerable<string> names)
        {
            var sorted = new List<string>(names);
            sorted.Sort(System.StringComparer.Ordinal);
            return string.Join(",", sorted);
        }

        // ---------------------------------------------------------------- simulation (owner only)

        protected override float OnTick()
        {
            Inventory inventory = GetInventory();
            if (inventory == null || inventory.NrOfItems() == 0)
                return NextIdleDelay(IdleInterval);

            CollectTargets(m_targets);
            m_targets.Sort((a, b) => a.SqrDistance.CompareTo(b.SqrDistance));

            // Fuel first, so a kiln can't eat the wood the fires need; inputs get whatever is left.
            foreach (Target target in m_targets)
            {
                if (!target.IsInput && target.Room > 0 && !IsDenied(target.Station.Prefab))
                    DeliverFuel(inventory, target);
            }
            foreach (Target target in m_targets)
            {
                if (target.IsInput && target.Room > 0 && !IsDenied(target.Station.Prefab))
                    DeliverInputs(inventory, target);
            }

            m_targets.Clear();
            return NextIdleDelay(IdleInterval);
        }

        /// <summary>
        /// Fuel, up to the free room. Most stations take one fuel item; a shield generator takes any of several,
        /// used in the station's own order.
        /// </summary>
        private void DeliverFuel(Inventory inventory, Target target)
        {
            int room = target.Room;
            foreach (ItemDrop fuel in target.Station.FuelItems)
            {
                if (room <= 0)
                    break;
                if (fuel)
                    room -= Deliver(inventory, target, WardRpcs.StationFuel, fuel.name, fuel.m_itemData.m_shared.m_name, room);
            }
        }

        /// <summary>Kiln-type station: load any inventory items it accepts, up to its free room.</summary>
        private void DeliverInputs(Inventory inventory, Target target)
        {
            Smelter smelter = target.Station.Smelter;
            int room = target.Room;

            // Snapshot the accepted item types first: delivering changes the inventory.
            var accepted = new List<KeyValuePair<string, string>>(); // prefab, shared name
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (!item.m_dropPrefab || !smelter.IsItemAllowed(item.m_dropPrefab.name))
                    continue;
                var entry = new KeyValuePair<string, string>(item.m_dropPrefab.name, item.m_shared.m_name);
                if (!accepted.Contains(entry))
                    accepted.Add(entry);
            }

            foreach (KeyValuePair<string, string> input in accepted)
            {
                if (room <= 0)
                    break;
                room -= Deliver(inventory, target, WardRpcs.StationInput, input.Key, input.Value, room);
            }
        }

        /// <summary>
        /// Takes up to <paramref name="room"/> of one item out of the inventory in one go and sends them to the
        /// station in one RPC. Returns how many were sent. If sending fails, they're put straight back.
        /// </summary>
        private int Deliver(Inventory inventory, Target target, string rpc, string itemPrefab, string sharedName, int room)
        {
            int count = Mathf.Min(room, inventory.CountItems(sharedName));
            if (count <= 0)
                return 0;

            inventory.RemoveItem(sharedName, count);
            try
            {
                target.Station.View.InvokeRPC(rpc, m_nview.GetZDO().m_uid, itemPrefab, count);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Refuel: sending {count} {itemPrefab} to {target.Station.Prefab} failed, keeping them. {e.Message}");
                ReturnItems(itemPrefab, count);
                return 0;
            }
            return count;
        }

        /// <summary>A station sent back items it couldn't use (full, changed owner, or an error).</summary>
        private void RPC_Refund(long sender, string itemPrefab, int count)
        {
            if (!IsValid || count <= 0)
                return;
            if (!m_nview.IsOwner())
            {
                // Ownership moved since the delivery: the new owner holds the inventory now.
                m_nview.InvokeRPC(WardRpcs.Refund, itemPrefab, count);
                return;
            }
            ReturnItems(itemPrefab, count);
        }

        /// <summary>Puts items back in the ward's inventory; anything that no longer fits is dropped beside it.</summary>
        private void ReturnItems(string itemPrefab, int count)
        {
            GameObject prefab = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(itemPrefab) : null;
            ItemDrop drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!drop)
            {
                Plugin.Log.LogWarning($"Refuel: couldn't return {count} {itemPrefab}: unknown item.");
                return;
            }

            Inventory inventory = GetInventory();
            int maxStack = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            while (count > 0)
            {
                int stack = Mathf.Min(count, maxStack);
                count -= stack;
                if (inventory != null && inventory.CanAddItem(prefab, stack) && inventory.AddItem(prefab, stack))
                    continue;

                ItemDrop.ItemData item = drop.m_itemData.Clone();
                item.m_dropPrefab = prefab;
                item.m_stack = stack;
                ItemDrop.DropItem(item, stack, transform.position + Vector3.up, Quaternion.identity);
                Plugin.Log.LogInfo($"Refuel: ward inventory full, dropped {stack} {itemPrefab} beside it.");
            }
        }

        /// <summary>
        /// Every station in range this ward can supply, on any client (the panel uses it too). Room is an
        /// estimate from the synced ZDO using vanilla's own "full" rules; the station's owner re-checks it.
        /// </summary>
        private void CollectTargets(List<Target> results)
        {
            results.Clear();
            Vector3 center = transform.position;
            float range = GetRange();
            float maxSqr = range * range;

            foreach (StationRegistry.Station station in StationRegistry.Stations)
            {
                // Horizontal distance: matches the ring on the ground.
                Vector3 offset = station.Component.transform.position - center;
                offset.y = 0f;
                float sqr = offset.sqrMagnitude;
                if (sqr > maxSqr)
                    continue;

                if (!station.View || !station.View.IsValid() || !station.Piece || !station.Piece.IsPlacedByPlayer())
                    continue;

                ZDO zdo = station.View.GetZDO();
                var target = new Target { Station = station, SqrDistance = sqr };
                bool takesFuel = station.MaxFuel > 0 && station.FuelItems.Count > 0;

                if (station.Kind == StationKind.Fireplace)
                {
                    Fireplace fireplace = station.Fireplace;
                    if (!takesFuel || !fireplace.m_canRefill || fireplace.m_infiniteFuel)
                        continue;
                }
                else if (station.Kind == StationKind.Smelter && !takesFuel)
                {
                    // Fuel-less smelters: only listed input stations (the kiln), never windmills etc.
                    Smelter smelter = station.Smelter;
                    if (smelter.m_maxOre <= 0 || !IsInputStation(station.Prefab))
                        continue;
                    target.IsInput = true;
                    target.Room = StationDelivery.InputRoom(smelter, zdo.GetInt(ZDOVars.s_queued));
                    results.Add(target);
                    continue;
                }
                else if (!takesFuel)
                {
                    continue;
                }

                target.Room = StationDelivery.FuelRoom(station, StationDelivery.GetFuel(station, zdo));
                results.Add(target);
            }
        }

        private static bool IsInputStation(string prefab)
        {
            string source = PluginConfig.RefuelInputStations.Value ?? "";
            if (source != s_inputStationsSource)
            {
                s_inputStationsSource = source;
                s_inputStations.Clear();
                s_inputStations.UnionWith(ParseNames(source));
            }
            return s_inputStations.Contains(prefab);
        }

        // ---------------------------------------------------------------- panel

        public override void PopulatePanel(RectTransform extras)
        {
            WardPanel.AddNote(extras, Localization.instance.Localize("$wp_refuel_targets"));
            // Long lists (many station types in range) scroll instead of stretching the panel down the screen.
            m_typeList = WardPanel.AddScrollGroup(extras, PanelListMaxRows).Content;
            m_typeListSignature = null;
            RebuildTypeListIfChanged();
        }

        public override void RefreshPanel()
        {
            if (!m_typeList)
                return;

            // Event-driven: rescan only when something that shapes the list changed (a station was added or
            // removed anywhere, the range, the deny list, or the input-station config). Rows are rebuilt only
            // if the result actually differs, so a click never lands on a row being replaced.
            IsDenied(""); // Refresh the cached deny list.
            if (StationRegistry.Version != m_listVersion
                || !Mathf.Approximately(GetRange(), m_listRange)
                || m_deniedSource != m_listDenied
                || PluginConfig.RefuelInputStations.Value != m_listInputs)
            {
                RebuildTypeListIfChanged();
            }

            foreach (KeyValuePair<string, Toggle> pair in m_typeToggles)
            {
                bool on = !IsDenied(pair.Key);
                if (pair.Value && pair.Value.isOn != on)
                    pair.Value.SetIsOnWithoutNotify(on);
            }
        }

        /// <summary>
        /// One row per station prefab type in range ("Campfire ×5"), plus denied types that aren't in range
        /// right now, so they can still be turned back on. Checked = refuel.
        /// </summary>
        private void RebuildTypeListIfChanged()
        {
            IsDenied(""); // Refresh the cached deny list.
            m_listVersion = StationRegistry.Version;
            m_listRange = GetRange();
            m_listDenied = m_deniedSource;
            m_listInputs = PluginConfig.RefuelInputStations.Value;

            var counts = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
            var names = new Dictionary<string, string>();
            CollectTargets(m_targets);
            foreach (Target target in m_targets)
            {
                string prefab = target.Station.Prefab;
                counts.TryGetValue(prefab, out int count);
                counts[prefab] = count + 1;
                if (!names.ContainsKey(prefab))
                    names[prefab] = Localization.instance.Localize(target.Station.Piece.m_name);
            }
            m_targets.Clear();

            IsDenied(""); // Refresh the cached deny set.
            foreach (string prefab in m_denied)
            {
                if (!counts.ContainsKey(prefab))
                    counts[prefab] = 0;
            }

            var signature = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in counts)
                signature.Append(pair.Key).Append(':').Append(pair.Value).Append(';');
            if (signature.ToString() == m_typeListSignature)
                return;
            m_typeListSignature = signature.ToString();

            WardPanel.ClearChildren(m_typeList);
            m_typeToggles.Clear();

            if (counts.Count == 0)
            {
                WardPanel.AddNote(m_typeList, Localization.instance.Localize("$wp_refuel_none"));
                return;
            }

            var rows = new List<KeyValuePair<string, string>>(); // prefab, label
            foreach (KeyValuePair<string, int> pair in counts)
            {
                string label = names.TryGetValue(pair.Key, out string display) ? display : PrefabDisplayName(pair.Key);
                if (pair.Value > 1)
                    label += $" ×{pair.Value}";
                rows.Add(new KeyValuePair<string, string>(pair.Key, label));
            }
            rows.Sort((a, b) => string.Compare(a.Value, b.Value, System.StringComparison.CurrentCultureIgnoreCase));

            foreach (KeyValuePair<string, string> row in rows)
            {
                string prefab = row.Key;
                m_typeToggles[prefab] = WardPanel.AddToggleRow(m_typeList, row.Value, !IsDenied(prefab),
                    on => RequestSetDenied(prefab, !on));
            }
        }

        /// <summary>Localized piece name for a prefab that isn't in range (from the prefab itself).</summary>
        private static string PrefabDisplayName(string prefab)
        {
            GameObject go = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefab) : null;
            Piece piece = go ? go.GetComponent<Piece>() : null;
            return piece ? Localization.instance.Localize(piece.m_name) : prefab;
        }
    }
}
