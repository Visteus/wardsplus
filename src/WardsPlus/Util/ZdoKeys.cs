using System.Collections.Generic;

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
        /// <summary>Station prefab types in priority order, highest first; unlisted types come after (string).</summary>
        public static readonly int Order = "WP_Order".GetStableHashCode();

        // Defender Ward
        /// <summary>Creature prefab the player picked for the current trophy (string). Empty: the best fit.</summary>
        public static readonly int Creature = "WP_Creature".GetStableHashCode();
        /// <summary>On the ward: the summon it currently has out (ZDOID). None when it has none.</summary>
        public static readonly KeyValuePair<int, int> Summon = ZDO.GetHashZDOID("WP_Summon");
        /// <summary>On the ward: ZNet time (ticks) of its last summon, for the cooldown (long).</summary>
        public static readonly int SummonTime = "WP_SummonTime".GetStableHashCode();
        /// <summary>On a summoned creature: the ward that summoned it (ZDOID). Marks it as a defender on every client.</summary>
        public static readonly KeyValuePair<int, int> SummonOf = ZDO.GetHashZDOID("WP_SummonOf");

        // Gathering Ward
        /// <summary>Radius (m) of the area the ward deposits into nearby containers from; 0 = off (float).</summary>
        public static readonly int DepositRange = "WP_DepositRange".GetStableHashCode();
        /// <summary>Comma-separated container prefab names this ward won't deposit into (string).</summary>
        public static readonly int DepositDenied = "WP_DepositDeny".GetStableHashCode();
        /// <summary>Set once the config's default container deny list has been stamped onto this ward (bool).</summary>
        public static readonly int DepositDeniedInit = "WP_DepositDenyInit".GetStableHashCode();
        /// <summary>Container prefab types in deposit priority order, highest first; unlisted types come after (string).</summary>
        public static readonly int DepositOrder = "WP_DepositOrder".GetStableHashCode();
        /// <summary>On a dropped item: a player dropped it, so Gathering Wards leave it alone (bool).</summary>
        public static readonly int PlayerDropped = "WP_PlayerDropped".GetStableHashCode();
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
        /// <summary>Asks a Refuel Ward's owner to move one station type above another in its priority order.</summary>
        public const string MoveType = "WP_MoveType";
        /// <summary>On a fire/smelter: (ward, fuel item prefab, count). Applied by the station's owner.</summary>
        public const string StationFuel = "WP_StationFuel";
        /// <summary>On a kiln-type station: (ward, input item prefab, count). Applied by the station's owner.</summary>
        public const string StationInput = "WP_StationInput";
        /// <summary>On a Refuel Ward: (item prefab, count) a station couldn't use, put back in the ward's inventory.</summary>
        public const string Refund = "WP_Refund";
        /// <summary>Asks a Defender Ward's owner to summon a different creature for its trophy (prefab name).</summary>
        public const string SetCreature = "WP_SetCreature";
        /// <summary>Broadcast by a Defender Ward: (position) play the summon "poof" there, on every client.</summary>
        public const string SummonFx = "WP_SummonFx";
        /// <summary>Asks a Gathering Ward's owner to deny or allow one container prefab type.</summary>
        public const string SetDepositDenied = "WP_SetDepositDenied";
        /// <summary>Asks a Gathering Ward's owner to move one container type above another in its deposit order.</summary>
        public const string MoveDepositType = "WP_MoveDepositType";
        /// <summary>On a container: (ward, packed items) to add onto matching stacks. Applied by the container's owner.</summary>
        public const string ContainerDeposit = "WP_ContainerDeposit";
        /// <summary>On a Gathering Ward: (packed items) a container couldn't take, put back in the ward's inventory.</summary>
        public const string DepositRefund = "WP_DepositRefund";
    }
}
