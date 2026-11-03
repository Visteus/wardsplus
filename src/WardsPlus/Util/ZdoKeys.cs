namespace WardsPlus.Util
{
    /// <summary>
    /// ZDO keys for per-ward state. The ZDO is the ward's network-synced save data, so anything stored
    /// here is shared with every client and survives restarts. Hashes are precomputed once.
    /// </summary>
    internal static class ZdoKeys
    {
        public static readonly int Enabled = "WP_Enabled".GetStableHashCode();
        public static readonly int Range = "WP_Range".GetStableHashCode();

        // Repair Ward
        /// <summary>Repairs left from the last fuel item consumed (int).</summary>
        public static readonly int Charges = "WP_Charges".GetStableHashCode();
        /// <summary>Only pieces below this health percentage (10-100) are repaired (float).</summary>
        public static readonly int RepairBelow = "WP_RepairBelow".GetStableHashCode();
    }

    /// <summary>RPC names registered on every ward's ZNetView.</summary>
    internal static class WardRpcs
    {
        public const string SetBool = "WP_SetBool";
        public const string SetFloat = "WP_SetFloat";
        /// <summary>Broadcast by a Repair Ward so every nearby client plays the repaired piece's effect.</summary>
        public const string RepairFx = "WP_RepairFx";
    }
}
