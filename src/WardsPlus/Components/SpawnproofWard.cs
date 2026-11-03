using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Prevents creature spawns inside its radius (Phase 3).
    /// Phase 1 stub: placeable and configurable through the shared panel; no ward-specific behaviour yet.
    /// </summary>
    public class SpawnproofWard : WardBase
    {
        public override WardType Type => WardType.Spawnproof;
    }
}
