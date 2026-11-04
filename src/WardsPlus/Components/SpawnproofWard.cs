using System.Collections.Generic;
using UnityEngine;
using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Stops creatures spawning inside its radius, with the same rules vanilla applies inside a player base
    /// (workbench area) but with a radius that is exact and identical on every machine.
    ///
    /// Why vanilla's upgraded-station radius is unreliable, and what this does instead:
    /// - Vanilla blocks spawns with a physics overlap against the station's trigger collider. That collider
    ///   only exists on machines that have the station loaded, and its radius only grows to the upgraded size
    ///   when that machine happens to recount the station's extensions (on use/hover). So the machine
    ///   deciding a spawn may still see the base 20m, or nothing at all.
    /// - Here the radius is the ward's own synced setting (ZDO), read at the moment of the check, and the
    ///   check is plain math: horizontal distance to the ward, matching the ring drawn on the ground.
    ///   No colliders, nothing computed lazily, the same answer on every machine.
    /// - The ward is a "distant" network object (set on the prefab), so its data and object reach clients
    ///   two zones further out than normal pieces. Spawns are decided by whichever client simulates a zone
    ///   and can be 40-80m from a player, so this keeps the ward known wherever a spawn near it is decided.
    ///
    /// Blocks: world spawns and raid/event spawns (SpawnSystem), and fixed creature spawn points
    /// (CreatureSpawner). Like vanilla bases, it respects spawns that are explicitly allowed inside bases,
    /// and leaves nests (SpawnArea) alone. No inventory and nothing to simulate.
    /// </summary>
    public class SpawnproofWard : WardBase
    {
        public override WardType Type => WardType.Spawnproof;

        /// <summary>Nothing to simulate; the tick loop just idles.</summary>
        protected override float IdleInterval => 600f;

        private static readonly List<SpawnproofWard> s_loaded = new List<SpawnproofWard>();

        protected override void Awake()
        {
            base.Awake();
            if (IsValid)
                s_loaded.Add(this);
        }

        protected override void OnDestroy()
        {
            s_loaded.Remove(this);
            base.OnDestroy();
        }

        /// <summary>Whether <paramref name="point"/> is inside any enabled Spawnproof Ward's radius.</summary>
        public static bool IsProtected(Vector3 point)
        {
            for (int i = 0; i < s_loaded.Count; i++)
            {
                SpawnproofWard ward = s_loaded[i];
                if (!ward || !ward.IsValid || !ward.IsEnabled())
                    continue;

                // Horizontal distance: the protected area is the column above and below the ring.
                Vector3 offset = point - ward.transform.position;
                float range = ward.GetRange();
                if (offset.x * offset.x + offset.z * offset.z <= range * range)
                    return true;
            }
            return false;
        }
    }
}
