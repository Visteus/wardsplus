using System;
using System.Collections.Generic;
using UnityEngine;
using WardsPlus.Util;
using Object = UnityEngine.Object;

namespace WardsPlus.Components
{
    /// <summary>Which vanilla component a station is, and so how it is supplied.</summary>
    internal enum StationKind
    {
        /// <summary>Fires, hearths, braziers, sconces, torches, hot tub (Fireplace).</summary>
        Fireplace,
        /// <summary>Smelter, blast furnace, eitr refinery (fuel), kiln (input) (Smelter).</summary>
        Smelter,
        /// <summary>Stone oven and any other fuel-burning cooking station (CookingStation with m_useFuel).</summary>
        Oven,
        /// <summary>Shield generator; accepts any of several fuel items (ShieldGenerator).</summary>
        ShieldGenerator,
    }

    /// <summary>
    /// Every loaded fuel-using station on this client, so Refuel Wards scan a few dozen stations instead of
    /// every loaded piece. Also gives each station the bulk add RPCs the wards use. Generic: it goes by
    /// vanilla component, not prefab name, so modded pieces built on these components are included.
    ///
    /// Lifecycle:
    /// - Added from each component's own setup (Harmony postfix on Fireplace/Smelter/CookingStation.Awake,
    ///   ShieldGenerator.Start). That runs whenever the game creates the object: placing it, a zone loading,
    ///   or loading a save. So after a reload every station is re-added as its zone loads, exactly when
    ///   vanilla's own code for it starts running. Placement previews (ghosts) have no network object and
    ///   are skipped.
    /// - Removed when the game destroys it (removed, zone unloaded, logout): every one of those destroys the
    ///   station's GameObject, and a small <see cref="StationTracker"/> on it reports that from OnDestroy.
    ///   As a fallback, anything that still turns up Unity-null is dropped when the list is read.
    /// - Cleared when the network scene is destroyed (logout), so nothing carries over between worlds.
    /// - Safety net: the first read in each world also picks up any station that was already loaded before
    ///   the hooks existed (e.g. the mod was hot-reloaded mid-game).
    /// <see cref="Version"/> changes on every add/remove, so open panels only rescan when something changed.
    ///
    /// Max fuel and fuel items are read live from the component, never cached: mods that change a smelter's
    /// or fire's capacity (on the prefab, after Awake, or on a config reload) are always respected.
    /// </summary>
    internal static class StationRegistry
    {
        internal sealed class Station
        {
            public StationKind Kind;
            public MonoBehaviour Component;
            public Piece Piece;
            public ZNetView View;
            public string Prefab;

            private ItemDrop _cachedSingleFuel;
            private ItemDrop[] _cachedSingleFuelList = NoFuel;

            /// <summary>Current fuel capacity, straight from the component (mods may change it at any time).</summary>
            public float MaxFuel
            {
                get
                {
                    switch (Kind)
                    {
                        case StationKind.Fireplace: return Fireplace.m_maxFuel;
                        case StationKind.Smelter: return Smelter.m_maxFuel;
                        case StationKind.Oven: return Oven.m_maxFuel;
                        case StationKind.ShieldGenerator: return Shield.m_maxFuel;
                        default: return 0f;
                    }
                }
            }

            /// <summary>Fuel items it accepts, in its own order of preference (empty for input-only smelters).</summary>
            public IReadOnlyList<ItemDrop> FuelItems
            {
                get
                {
                    ItemDrop single;
                    switch (Kind)
                    {
                        case StationKind.ShieldGenerator:
                            return Shield.m_fuelItems ?? (IReadOnlyList<ItemDrop>)NoFuel;
                        case StationKind.Fireplace: single = Fireplace.m_fuelItem; break;
                        case StationKind.Smelter: single = Smelter.m_fuelItem; break;
                        case StationKind.Oven: single = Oven.m_fuelItem; break;
                        default: return NoFuel;
                    }
                    if (single != _cachedSingleFuel)
                    {
                        _cachedSingleFuel = single;
                        _cachedSingleFuelList = single ? new[] { single } : NoFuel;
                    }
                    return _cachedSingleFuelList;
                }
            }

            public Fireplace Fireplace => Component as Fireplace;
            public Smelter Smelter => Component as Smelter;
            public CookingStation Oven => Component as CookingStation;
            public ShieldGenerator Shield => Component as ShieldGenerator;

            public bool AcceptsFuel(string itemPrefab)
            {
                foreach (ItemDrop item in FuelItems)
                {
                    if (item && item.name == itemPrefab)
                        return true;
                }
                return false;
            }
        }

        private static readonly ItemDrop[] NoFuel = new ItemDrop[0];

        private static readonly List<Station> s_stations = new List<Station>();
        private static readonly HashSet<MonoBehaviour> s_tracked = new HashSet<MonoBehaviour>();
        /// <summary>
        /// One station per network object: the RPCs live on the ZNetView and can only be registered once, so
        /// if a piece had two fuel components (e.g. a fire and an oven) the first one tracked handles it.
        /// </summary>
        private static readonly HashSet<ZNetView> s_views = new HashSet<ZNetView>();
        private static ZNetScene s_seededFor;

        /// <summary>Changes whenever a station is added or removed.</summary>
        public static int Version { get; private set; }

        /// <summary>Live stations. Don't keep the list across frames.</summary>
        public static List<Station> Stations
        {
            get
            {
                EnsureSeeded();
                // Fallback only: the tracker normally removes stations the moment they're destroyed.
                for (int i = s_stations.Count - 1; i >= 0; i--)
                {
                    if (!s_stations[i].Component)
                        RemoveAt(i);
                }
                return s_stations;
            }
        }

        // Each uses the ZNetView the station resolved for itself during its own setup (some fall back to a parent's).

        public static void Track(Fireplace fireplace)
        {
            if (fireplace)
                Track(fireplace, StationKind.Fireplace, fireplace.m_nview);
        }

        public static void Track(Smelter smelter)
        {
            if (smelter)
                Track(smelter, StationKind.Smelter, smelter.m_nview);
        }

        public static void Track(CookingStation station)
        {
            // Plain cooking stations (spit, iron cooking station) burn nothing; only fuel-using ones count.
            if (station && station.m_useFuel)
                Track(station, StationKind.Oven, station.m_nview);
        }

        public static void Track(ShieldGenerator shield)
        {
            if (shield)
                Track(shield, StationKind.ShieldGenerator, shield.m_nview);
        }

        /// <summary>Called by <see cref="StationTracker"/> when the station's GameObject is destroyed.</summary>
        public static void Untrack(MonoBehaviour component)
        {
            if (!s_tracked.Contains(component))
                return;
            for (int i = s_stations.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_stations[i].Component, component))
                    RemoveAt(i);
            }
        }

        public static void Clear()
        {
            s_stations.Clear();
            s_tracked.Clear();
            s_views.Clear();
            s_seededFor = null;
            Version++;
        }

        private static void RemoveAt(int index)
        {
            Station station = s_stations[index];
            s_tracked.Remove(station.Component);
            s_views.Remove(station.View);
            if (station.View)
            {
                // Only reachable if the component went away but the object didn't: free the names for re-tracking.
                station.View.Unregister(WardRpcs.StationFuel);
                if (station.Kind == StationKind.Smelter)
                    station.View.Unregister(WardRpcs.StationInput);
            }
            s_stations[index] = s_stations[s_stations.Count - 1];
            s_stations.RemoveAt(s_stations.Count - 1);
            Version++;
        }

        private static void Track(MonoBehaviour component, StationKind kind, ZNetView view)
        {
            if (!view || view.GetZDO() == null)
                return; // Placement ghost or prefab: not a real, networked station.
            if (s_tracked.Contains(component) || s_views.Contains(view))
                return;
            s_tracked.Add(component);
            s_views.Add(view);

            // The piece and prefab name belong to the networked root, which may be a parent of the component.
            Piece piece = view.GetComponent<Piece>();
            if (!piece)
                piece = component.GetComponentInParent<Piece>();

            var station = new Station
            {
                Kind = kind,
                Component = component,
                Piece = piece,
                View = view,
                Prefab = Utils.GetPrefabName(view.gameObject),
            };
            s_stations.Add(station);
            Version++;

            // Reports the station's destruction (removal, zone unload, logout) straight away.
            component.gameObject.AddComponent<StationTracker>().Station = component;

            view.Register<ZDOID, string, int>(WardRpcs.StationFuel,
                (sender, ward, item, count) => StationDelivery.OnFuel(station, sender, ward, item, count));
            if (kind == StationKind.Smelter)
            {
                view.Register<ZDOID, string, int>(WardRpcs.StationInput,
                    (sender, ward, item, count) => StationDelivery.OnInput(station, sender, ward, item, count));
            }
        }

        private static void EnsureSeeded()
        {
            ZNetScene scene = ZNetScene.instance;
            if (!scene || s_seededFor == scene)
                return;
            s_seededFor = scene;
            foreach (Fireplace fireplace in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
                Track(fireplace);
            foreach (Smelter smelter in Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
                Track(smelter);
            foreach (CookingStation oven in Object.FindObjectsByType<CookingStation>(FindObjectsSortMode.None))
                Track(oven);
            foreach (ShieldGenerator shield in Object.FindObjectsByType<ShieldGenerator>(FindObjectsSortMode.None))
                Track(shield);
        }
    }

    /// <summary>Sits on a tracked station's GameObject and tells the registry when the game destroys it.</summary>
    internal sealed class StationTracker : MonoBehaviour
    {
        public MonoBehaviour Station;

        private void OnDestroy() => StationRegistry.Untrack(Station);
    }

    /// <summary>
    /// Applies a Refuel Ward's bulk delivery on the machine that owns the station, which is the only one
    /// allowed to change its fuel or queue. The amount is clamped against the station's real, current state
    /// there, so overlapping wards can never overfill it; anything that doesn't fit (or any failure) is sent
    /// back to the ward that delivered it.
    /// </summary>
    internal static class StationDelivery
    {
        /// <summary>Current fuel, as each vanilla component reads it.</summary>
        public static float GetFuel(StationRegistry.Station station, ZDO zdo) =>
            station.Kind == StationKind.ShieldGenerator
                ? zdo.GetFloat(ZDOVars.s_fuel, station.Shield.m_defaultFuel)
                : zdo.GetFloat(ZDOVars.s_fuel);

        /// <summary>
        /// How many fuel items the station takes now, by vanilla's own "full" rules: a fire refuses once
        /// Ceil(fuel) >= max; smelters, ovens and shield generators are full once fuel > max - 1.
        /// </summary>
        public static int FuelRoom(StationRegistry.Station station, float fuel)
        {
            float max = station.MaxFuel; // Live: respects mods that raise or lower capacity.
            int room = station.Kind == StationKind.Fireplace
                ? Mathf.FloorToInt(max) - Mathf.CeilToInt(fuel)
                : Mathf.FloorToInt(max - fuel);
            return Mathf.Max(0, room);
        }

        /// <summary>How many input items (e.g. wood for a kiln) a smelter's queue takes now.</summary>
        public static int InputRoom(Smelter smelter, int queued) => Mathf.Max(0, smelter.m_maxOre - queued);

        public static void OnFuel(StationRegistry.Station station, long sender, ZDOID ward, string item, int count)
        {
            int applied = 0;
            try
            {
                applied = ApplyFuel(station, item, count);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Refuel: adding {count} {item} to {station.Prefab} failed, returning it to the ward. {e.Message}");
            }
            if (DebugLog.Enabled)
                DebugLog.Write($"{DebugLog.Name(station.Prefab, station.View)}: took {applied} of {count} {item} as fuel from a Refuel Ward (peer {sender}).");
            RefundRest(sender, ward, item, count, applied);
        }

        public static void OnInput(StationRegistry.Station station, long sender, ZDOID ward, string item, int count)
        {
            int applied = 0;
            try
            {
                applied = ApplyInput(station.Smelter, item, count);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Refuel: loading {count} {item} into {station.Prefab} failed, returning it to the ward. {e.Message}");
            }
            if (DebugLog.Enabled)
                DebugLog.Write($"{DebugLog.Name(station.Prefab, station.View)}: loaded {applied} of {count} {item} from a Refuel Ward (peer {sender}).");
            RefundRest(sender, ward, item, count, applied);
        }

        private static int ApplyFuel(StationRegistry.Station station, string item, int count)
        {
            ZNetView view = station.View;
            if (!station.Component || !view || !view.IsValid() || !view.IsOwner() || !station.AcceptsFuel(item))
                return 0;

            ZDO zdo = view.GetZDO();
            float fuel = GetFuel(station, zdo);
            int applied = Mathf.Min(count, FuelRoom(station, fuel));
            if (applied <= 0)
                return 0;

            float newFuel = station.Kind == StationKind.Fireplace
                ? Mathf.Clamp(fuel + applied, 0f, station.MaxFuel)
                : fuel + applied;
            zdo.Set(ZDOVars.s_fuel, newFuel);

            Transform t = station.Component.transform;
            PlayEffects(() =>
            {
                switch (station.Kind)
                {
                    case StationKind.Fireplace:
                        station.Fireplace.m_fuelAddedEffects.Create(t.position, t.rotation);
                        station.Fireplace.UpdateState();
                        break;
                    case StationKind.Smelter:
                        station.Smelter.m_fuelAddedEffects.Create(t.position, t.rotation, t);
                        break;
                    case StationKind.Oven:
                        station.Oven.m_fuelAddedEffects.Create(t.position, t.rotation, t);
                        break;
                    case StationKind.ShieldGenerator:
                        station.Shield.m_fuelAddedEffects.Create(t.position, t.rotation, t);
                        break;
                }
            });
            return applied;
        }

        private static int ApplyInput(Smelter smelter, string item, int count)
        {
            ZNetView view = smelter ? smelter.m_nview : null;
            if (!view || !view.IsValid() || !view.IsOwner() || !smelter.IsItemAllowed(item))
                return 0;

            int applied = Mathf.Min(count, InputRoom(smelter, smelter.GetQueueSize()));
            for (int i = 0; i < applied; i++)
                smelter.QueueOre(item, false);
            if (applied > 0)
                PlayEffects(() => smelter.m_oreAddedEffects.Create(smelter.transform.position, smelter.transform.rotation));
            return applied;
        }

        /// <summary>Effects are cosmetic: a failure there must not refund items that were already added.</summary>
        private static void PlayEffects(Action play)
        {
            try
            {
                play();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Refuel: station effect failed: {e.Message}");
            }
        }

        /// <summary>
        /// Sends what wasn't used back to the ward, on the machine that sent it (it certainly has the ward
        /// loaded; the ward forwards it to its current owner if that changed in the meantime).
        /// </summary>
        private static void RefundRest(long sender, ZDOID ward, string item, int count, int applied)
        {
            int rest = count - Mathf.Max(0, applied);
            if (rest <= 0)
                return;
            if (ZRoutedRpc.instance == null || ward.IsNone())
            {
                Plugin.Log.LogWarning($"Refuel: couldn't return {rest} {item} to its ward.");
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ward, WardRpcs.Refund, item, rest);
        }
    }
}
