using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WardsPlus.Config;
using WardsPlus.UI;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// Repairs damaged player-built pieces in range, burning fuel items from its inventory.
    /// - Idle: checks for damage every Scan Interval (jittered), or sooner when fuel is added.
    /// - Active: while anything in range needs repair, fully repairs the nearest piece every Repair Interval,
    ///   using one charge. Charges come from fuel items (e.g. 1 Resin = 10 repairs) and are stored in the
    ///   ZDO, so a partly used item is never lost to a reload or an ownership change.
    /// - "Repair below" (per ward) skips lightly damaged pieces, so rain wear doesn't eat fuel; pieces on the
    ///   Always Repair list (stakes) ignore it.
    /// Range is measured horizontally, matching the ring on the ground, so tall builds are fully covered.
    /// </summary>
    public class RepairWard : WardBase
    {
        public override WardType Type => WardType.Repair;

        protected override float IdleInterval => PluginConfig.RepairScanInterval.Value;

        private static float ActiveInterval => Mathf.Max(0.25f, PluginConfig.RepairInterval.Value);

        private const float MinRepairBelow = 10f;
        private const float MaxRepairBelow = 100f;
        /// <summary>
        /// A piece just repaired is skipped this long: its health update may take a network round trip
        /// (it's applied by the piece's owner), and picking it again would waste a charge.
        /// </summary>
        private const float RecentRepairWindow = 3f;
        private const float FuelAddedTickDelay = 1f;

        private readonly Dictionary<ZDOID, float> m_recentlyRepaired = new Dictionary<ZDOID, float>();
        private readonly List<ZDOID> m_expired = new List<ZDOID>();
        private bool m_repairing;

        // ---------------------------------------------------------------- lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (!IsValid)
                return;
            m_nview.Register<ZDOID>(WardRpcs.RepairFx, RPC_RepairFx);
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
            if (!zdo.GetFloat(ZdoKeys.RepairBelow, out _))
                zdo.Set(ZdoKeys.RepairBelow, ClampSetting(ZdoKeys.RepairBelow, PluginConfig.RepairDefaultThreshold.Value));
        }

        protected override float ClampSetting(int key, float value) =>
            key == ZdoKeys.RepairBelow ? Mathf.Clamp(value, MinRepairBelow, MaxRepairBelow) : base.ClampSetting(key, value);

        private Inventory GetInventory() => m_container ? m_container.GetInventory() : null;

        /// <summary>Fuel added to an idle ward: check for work soon instead of waiting out the scan interval.</summary>
        private void OnInventoryChanged()
        {
            if (!m_repairing && IsValid && m_nview.IsOwner() && IsEnabled())
                RequestTickSoon(FuelAddedTickDelay);
        }

        // ---------------------------------------------------------------- settings / state

        public int GetCharges() => IsValid ? m_nview.GetZDO().GetInt(ZdoKeys.Charges, 0) : 0;

        private void SetCharges(int charges) => m_nview.GetZDO().Set(ZdoKeys.Charges, Mathf.Max(0, charges));

        /// <summary>Health % below which pieces get repaired (10-100).</summary>
        public float GetRepairBelow() => GetFloatSetting(ZdoKeys.RepairBelow, PluginConfig.RepairDefaultThreshold.Value);

        /// <summary>Repairs the fuel items in the inventory are worth (not yet burned).</summary>
        public int GetStoredCharges()
        {
            Inventory inventory = GetInventory();
            if (inventory == null)
                return 0;

            int total = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                int charges = RepairFuel.ChargesFor(item);
                if (charges > 0)
                    total += charges * item.m_stack;
            }
            return total;
        }

        // ---------------------------------------------------------------- simulation (owner only)

        protected override float OnTick()
        {
            PruneRecentlyRepaired();

            WearNTear target = FindRepairTarget();
            if (!target || !TryEnsureCharge())
            {
                // Nothing to do, or out of fuel: back to slow scanning (adding fuel wakes it early).
                m_repairing = false;
                return NextIdleDelay(IdleInterval);
            }

            m_repairing = true;
            ZDOID id = target.m_nview.GetZDO().m_uid;
            m_recentlyRepaired[id] = Time.time;

            // Repair() sends the vanilla repair RPC to the piece's owner. It refuses if the piece was
            // repaired on this client less than a second ago; then no charge is spent.
            if (target.Repair())
            {
                SetCharges(GetCharges() - 1);
                m_nview.InvokeRPC(ZNetView.Everybody, WardRpcs.RepairFx, id);
            }
            return ActiveInterval;
        }

        /// <summary>The nearest player-built piece in range that needs repair, or null.</summary>
        private WearNTear FindRepairTarget()
        {
            Vector3 center = transform.position;
            float range = GetRange();
            float bestSqr = range * range;
            float threshold = GetRepairBelow() / 100f;
            WearNTear best = null;

            foreach (WearNTear wnt in WearNTear.GetAllInstances())
            {
                if (!wnt)
                    continue;

                // Horizontal distance: matches the ring on the ground, covers tall builds.
                Vector3 offset = wnt.transform.position - center;
                offset.y = 0f;
                float sqr = offset.sqrMagnitude;
                if (sqr > bestSqr)
                    continue;

                ZNetView nview = wnt.m_nview;
                if (!nview || !nview.IsValid() || !wnt.m_piece || !wnt.m_piece.IsPlacedByPlayer())
                    continue;

                // Read health straight from the ZDO (what Repair() checks), not the locally cached percentage.
                ZDO zdo = nview.GetZDO();
                float health = zdo.GetFloat(ZDOVars.s_health, wnt.m_health);
                if (health <= 0f || health >= wnt.m_health)
                    continue;
                if (health / wnt.m_health >= threshold && !RepairFuel.IsAlwaysRepaired(wnt))
                    continue;
                if (m_recentlyRepaired.ContainsKey(zdo.m_uid))
                    continue;

                best = wnt;
                bestSqr = sqr;
            }
            return best;
        }

        /// <summary>Makes sure at least one charge is available, burning one fuel item if needed.</summary>
        private bool TryEnsureCharge()
        {
            if (GetCharges() > 0)
                return true;

            Inventory inventory = GetInventory();
            if (inventory == null)
                return false;

            // Fuel types are used in config order.
            foreach (RepairFuel.Entry fuel in RepairFuel.Entries)
            {
                foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                {
                    if (item.m_shared.m_name != fuel.SharedName)
                        continue;
                    if (!inventory.RemoveOneItem(item))
                        return false;
                    SetCharges(fuel.Charges);
                    return true;
                }
            }
            return false;
        }

        private void PruneRecentlyRepaired()
        {
            if (m_recentlyRepaired.Count == 0)
                return;
            float now = Time.time;
            foreach (KeyValuePair<ZDOID, float> pair in m_recentlyRepaired)
            {
                if (now - pair.Value > RecentRepairWindow)
                    m_expired.Add(pair.Key);
            }
            foreach (ZDOID id in m_expired)
                m_recentlyRepaired.Remove(id);
            m_expired.Clear();
        }

        /// <summary>Every client: play the vanilla repair effect on the repaired piece, if it's loaded here.</summary>
        private void RPC_RepairFx(long sender, ZDOID pieceId)
        {
            if (!ZNetScene.instance)
                return;
            GameObject go = ZNetScene.instance.FindInstance(pieceId);
            Piece piece = go ? go.GetComponent<Piece>() : null;
            if (piece)
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);
        }

        // ---------------------------------------------------------------- hover + panel

        protected override void AppendHoverStatus(StringBuilder sb)
        {
            // One number: the charges left from the burning item plus what the fuel in the inventory is worth.
            sb.Append($"\n$wp_repair_charges: {GetCharges() + GetStoredCharges()}");

            float below = GetRepairBelow();
            if (below < MaxRepairBelow)
                sb.Append($"\n$wp_repair_below: {below:0}%");
        }

        public override void PopulatePanel(RectTransform extras)
        {
            WardPanel.AddSlider(extras, "$wp_repair_below", ZdoKeys.RepairBelow, MinRepairBelow, MaxRepairBelow,
                w => ((RepairWard)w).GetRepairBelow(), v => $"{v:0}%");
            WardPanel.AddNote(extras, RepairFuel.Describe());
        }
    }

    /// <summary>
    /// The Repair Ward's fuel and always-repair lists, parsed from config and cached until the config
    /// value (or the loaded ObjectDB) changes, so per-tick checks are dictionary lookups.
    /// </summary>
    internal static class RepairFuel
    {
        internal sealed class Entry
        {
            public string Prefab;
            /// <summary>The item's localization token (ItemData.m_shared.m_name), what inventories match on.</summary>
            public string SharedName;
            public int Charges;
        }

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly Dictionary<string, int> s_chargesByName = new Dictionary<string, int>();
        private static string s_fuelSource;
        private static ObjectDB s_fuelDb;

        private static readonly HashSet<string> s_alwaysRepair = new HashSet<string>();
        private static string s_alwaysSource;

        /// <summary>Valid fuel types in config order. Items unknown to this game (typos, removed mods) are skipped.</summary>
        public static List<Entry> Entries
        {
            get
            {
                string source = PluginConfig.RepairFuel.Value;
                if (source != s_fuelSource || s_fuelDb != ObjectDB.instance)
                    RebuildFuel(source);
                return s_entries;
            }
        }

        public static int ChargesFor(ItemDrop.ItemData item)
        {
            _ = Entries; // Refresh the cache if needed.
            return item != null && s_chargesByName.TryGetValue(item.m_shared.m_name, out int charges) ? charges : 0;
        }

        public static bool IsAlwaysRepaired(WearNTear wnt)
        {
            string source = PluginConfig.RepairAlwaysRepair.Value ?? "";
            if (source != s_alwaysSource)
            {
                s_alwaysSource = source;
                s_alwaysRepair.Clear();
                foreach (string raw in source.Split(','))
                {
                    string name = raw.Trim();
                    if (name.Length > 0)
                        s_alwaysRepair.Add(name);
                }
            }
            return s_alwaysRepair.Count > 0 && s_alwaysRepair.Contains(Utils.GetPrefabName(wnt.gameObject));
        }

        /// <summary>Localized "Fuel: Resin, Greydwarf eye" line for the panel.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder("$wp_repair_fuel: ");
            List<Entry> entries = Entries;
            if (entries.Count == 0)
                sb.Append("$wp_repair_fuel_none");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(entries[i].SharedName);
            }
            return Localization.instance.Localize(sb.ToString());
        }

        private static void RebuildFuel(string source)
        {
            s_fuelSource = source;
            s_fuelDb = ObjectDB.instance;
            s_entries.Clear();
            s_chargesByName.Clear();
            if (!s_fuelDb)
                return;

            foreach (KeyValuePair<string, int> pair in PluginConfig.ParseItemAmounts(PluginConfig.RepairFuel))
            {
                GameObject prefab = s_fuelDb.GetItemPrefab(pair.Key);
                ItemDrop drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (!drop)
                {
                    Plugin.Log.LogWarning($"[Repair Ward] Fuel: unknown item '{pair.Key}', skipping it.");
                    continue;
                }

                string sharedName = drop.m_itemData.m_shared.m_name;
                if (s_chargesByName.ContainsKey(sharedName))
                    continue; // Listed twice: the first entry wins.

                s_chargesByName[sharedName] = pair.Value;
                s_entries.Add(new Entry { Prefab = pair.Key, SharedName = sharedName, Charges = pair.Value });
            }
        }
    }
}
