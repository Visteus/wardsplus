using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Summons a friendly copy of the creature whose trophy it holds when enemies are near (Phase 5).
    /// Phase 1 stub: placeable and configurable through the shared panel; no ward-specific behaviour yet.
    /// </summary>
    public class DefenderWard : WardBase
    {
        public override WardType Type => WardType.Defender;
    }
}
