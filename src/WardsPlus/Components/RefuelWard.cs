using WardsPlus.Config;

namespace WardsPlus.Components
{
    /// <summary>
    /// Keeps nearby fires, sconces and smelters fueled from its inventory (Phase 4).
    /// Phase 1 stub: placeable and configurable through the shared panel; no ward-specific behaviour yet.
    /// </summary>
    public class RefuelWard : WardBase
    {
        public override WardType Type => WardType.Refuel;
    }
}
