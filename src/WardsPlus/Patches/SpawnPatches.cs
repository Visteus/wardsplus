using HarmonyLib;
using UnityEngine;
using WardsPlus.Components;

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
                __result = false;
        }

        /// <summary>
        /// Fixed creature spawn points. Skipping the update is what vanilla does for one inside a base: it just
        /// doesn't spawn while protected, and resumes if the ward is disabled or removed.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.UpdateSpawner))]
        private static bool UpdateSpawner(CreatureSpawner __instance) =>
            __instance.m_spawnInPlayerBase || !SpawnproofWard.IsProtected(__instance.transform.position);
    }
}
