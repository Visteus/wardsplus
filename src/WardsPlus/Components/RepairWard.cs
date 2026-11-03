using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Repairs damaged structures in range using fuel items from its inventory (Phase 2).
    /// Phase 1 stub: placeable and configurable through the shared panel; no ward-specific behaviour yet.
    /// </summary>
    public class RepairWard : WardBase
    {
        public override WardType Type => WardType.Repair;
    }
}
