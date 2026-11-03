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

        // Refuel Ward
        /// <summary>Comma-separated prefab names this ward won't refuel (string).</summary>
        public static readonly int Denied = "WP_Deny".GetStableHashCode();
        /// <summary>Set once the config's default deny list has been stamped onto this ward (bool).</summary>
        public static readonly int DeniedInit = "WP_DenyInit".GetStableHashCode();
    }

    /// <summary>RPC names registered on every ward's ZNetView.</summary>
    internal static class WardRpcs
    {
        public const string SetBool = "WP_SetBool";
        public const string SetFloat = "WP_SetFloat";
        /// <summary>Broadcast by a Repair Ward so every nearby client plays the repaired piece's effect.</summary>
        public const string RepairFx = "WP_RepairFx";
        /// <summary>Asks a Refuel Ward's owner to deny or allow one prefab type.</summary>
        public const string SetDenied = "WP_SetDenied";
        /// <summary>On a fire/smelter: (ward, fuel item prefab, count). Applied by the station's owner.</summary>
        public const string StationFuel = "WP_StationFuel";
        /// <summary>On a kiln-type station: (ward, input item prefab, count). Applied by the station's owner.</summary>
        public const string StationInput = "WP_StationInput";
        /// <summary>On a Refuel Ward: (item prefab, count) a station couldn't use, put back in the ward's inventory.</summary>
        public const string Refund = "WP_Refund";
    }
}
