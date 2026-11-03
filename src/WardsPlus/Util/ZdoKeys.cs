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
    }

    /// <summary>RPC names registered on every ward's ZNetView.</summary>
    internal static class WardRpcs
    {
        public const string SetBool = "WP_SetBool";
        public const string SetFloat = "WP_SetFloat";
    }
}
