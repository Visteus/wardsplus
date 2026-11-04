using System.Collections.Generic;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// A per-ward priority order of prefab types (Refuel: stations, Gathering: containers), set with the ▲/▼
    /// arrows in the ward's type list and stored in the ward's ZDO as an ordered comma-separated string.
    /// - Only types a player has moved are ranked; every other type comes after them, all equal, so a ward
    ///   nobody reorders keeps plain nearest-first behaviour. Wards sort by (rank, distance): distance still
    ///   decides between instances of the same type, and between unranked types.
    /// - A move is one "put this type directly above that one" request to the ward's owner, so two players
    ///   moving different types at once both keep their change.
    /// - Reads are cached and only re-parsed when the ZDO string changes.
    /// </summary>
    internal sealed class PrefabPriorityList
    {
        public const int Unranked = int.MaxValue;

        private readonly int m_key;
        private readonly string m_rpc;
        private ZNetView m_nview;

        private readonly Dictionary<string, int> m_ranks = new Dictionary<string, int>();
        private string m_source;

        /// <param name="zdoKey">ZDO string holding the order.</param>
        /// <param name="rpc">RPC name for "move one type above another" (registered on the ward's ZNetView).</param>
        public PrefabPriorityList(int zdoKey, string rpc)
        {
            m_key = zdoKey;
            m_rpc = rpc;
        }

        public void Register(ZNetView view)
        {
            m_nview = view;
            view.Register<string, string, string>(m_rpc, RPC_MoveAbove);
        }

        private bool IsValid => m_nview != null && m_nview.IsValid();

        /// <summary>The ZDO string the cache was last built from; changes whenever the order does.</summary>
        public string Source
        {
            get
            {
                Refresh();
                return m_source;
            }
        }

        /// <summary>0 for the top type; <see cref="Unranked"/> for types nobody has moved.</summary>
        public int RankOf(string prefab)
        {
            Refresh();
            return prefab != null && m_ranks.TryGetValue(prefab, out int rank) ? rank : Unranked;
        }

        public bool IsRanked(string prefab) => RankOf(prefab) != Unranked;

        /// <summary>Sort order for targets: higher priority first, then nearer first.</summary>
        public int Compare(string prefabA, float sqrDistanceA, string prefabB, float sqrDistanceB)
        {
            int byRank = RankOf(prefabA).CompareTo(RankOf(prefabB));
            return byRank != 0 ? byRank : sqrDistanceA.CompareTo(sqrDistanceB);
        }

        /// <summary>
        /// Asks the owner to put <paramref name="prefab"/> directly above <paramref name="anchor"/>.
        /// <paramref name="rankFirst"/>: unranked types shown above it, in list order, which get ranked first so
        /// the result matches what the player saw. Safe to call from any client.
        /// </summary>
        public void RequestMoveAbove(string prefab, string anchor, IEnumerable<string> rankFirst)
        {
            if (IsValid && !string.IsNullOrEmpty(prefab) && !string.IsNullOrEmpty(anchor) && prefab != anchor)
                m_nview.InvokeRPC(m_rpc, prefab, anchor, string.Join(",", rankFirst));
        }

        private void Refresh()
        {
            string source = IsValid ? m_nview.GetZDO().GetString(m_key) : "";
            if (source == m_source)
                return;
            m_source = source;
            m_ranks.Clear();
            foreach (string name in NameLists.Parse(source))
            {
                if (!m_ranks.ContainsKey(name))
                    m_ranks[name] = m_ranks.Count;
            }
        }

        private void RPC_MoveAbove(long sender, string prefab, string anchor, string rankFirst)
        {
            if (!m_nview.IsOwner() || string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(anchor) || prefab == anchor)
                return;

            var order = new List<string>();
            foreach (string name in NameLists.Parse(m_nview.GetZDO().GetString(m_key)))
            {
                if (!order.Contains(name))
                    order.Add(name);
            }
            foreach (string name in NameLists.Parse(rankFirst ?? ""))
            {
                if (name != prefab && !order.Contains(name))
                    order.Add(name);
            }
            if (!order.Contains(anchor))
                order.Add(anchor);

            order.Remove(prefab);
            order.Insert(order.IndexOf(anchor), prefab);
            string result = string.Join(",", order);
            m_nview.GetZDO().Set(m_key, result);
            DebugLog.Write($"{DebugLog.Name("Ward", m_nview)}: moved {prefab} above {anchor} (by peer {sender}). Order: {result}");
        }
    }
}
