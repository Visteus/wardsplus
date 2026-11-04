using HarmonyLib;
using UnityEngine;
using WardsPlus.Components;
using WardsPlus.Util;

namespace WardsPlus.Patches
{
    /// <summary>
    /// Spawnproof Ward hooks. They sit next to vanilla's own player-base check and use the same exceptions,
    /// so a ward behaves *mostly* like a base area, but isn't one.
    /// </summary>
    [HarmonyPatch]
    internal static class SpawnPatches
    {
        /// <summary>
        /// World and raid/event spawns. Vanilla checks every candidate point here (the spawn centre and each
        /// group member), so nothing in a group can land inside a ward.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.IsSpawnPointGood))]
        private static void IsSpawnPointGood(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
        {
            if (__result && !spawn.m_insidePlayerBase && SpawnproofWard.IsProtected(spawnPoint))
            {
                __result = false;
                if (DebugLog.Enabled)
                {
                    string creature = spawn.m_prefab ? spawn.m_prefab.name : "?";
                    DebugLog.WriteThrottled("spawn:" + creature, 10f,
                        $"Spawnproof: blocked a world/raid spawn of {creature} at {spawnPoint:F0} (repeats hidden for 10s).");
                }
            }
        }

        /// <summary>
        /// Fixed creature spawn points. Skipping the update is what vanilla does for one inside a base: it just
        /// doesn't spawn while protected, and resumes if the ward is disabled or removed.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.UpdateSpawner))]
        private static bool UpdateSpawner(CreatureSpawner __instance)
        {
            if (__instance.m_spawnInPlayerBase || !SpawnproofWard.IsProtected(__instance.transform.position))
                return true;
            if (DebugLog.Enabled)
            {
                string creature = __instance.m_creaturePrefab ? __instance.m_creaturePrefab.name : "?";
                DebugLog.WriteThrottled("spawner:" + __instance.GetInstanceID(), 60f,
                    $"Spawnproof: holding back a {creature} spawn point at {__instance.transform.position:F0} (repeats hidden for 60s).");
            }
            return false;
        }
    }
}
