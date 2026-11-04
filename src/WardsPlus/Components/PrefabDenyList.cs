using System;
using System.Collections.Generic;
using WardsPlus.Util;

namespace WardsPlus.Components
{
    /// <summary>
    /// A per-ward set of prefab names the ward leaves alone (Refuel: stations, Gathering: containers), stored in
    /// the ward's ZDO as a sorted comma-separated string so it syncs and persists.
    /// - New wards get the config's default list once (guarded by an "init" flag), so later config changes
    ///   don't rewrite wards players already set up.
    /// - Changes go to the ward's owner one type at a time, so two players toggling different types at once
    ///   can't overwrite each other.
    /// - Reads are cached and only re-parsed when the ZDO string changes.
    /// </summary>
    internal sealed class PrefabDenyList
    {
        private readonly int m_key;
        private readonly int m_initKey;
        private readonly string m_rpc;

        private ZNetView m_nview;
        private Action m_onAllowed;

        private readonly HashSet<string> m_denied = new HashSet<string>();
        private string m_source;

        /// <param name="zdoKey">ZDO string holding the list.</param>
        /// <param name="initKey">ZDO bool set once the config default has been stamped.</param>
        /// <param name="rpc">RPC name for "deny or allow one prefab" (registered on the ward's ZNetView).</param>
        public PrefabDenyList(int zdoKey, int initKey, string rpc)
        {
            m_key = zdoKey;
            m_initKey = initKey;
            m_rpc = rpc;
        }

        /// <summary>
        /// Registers the change RPC. <paramref name="onAllowed"/> runs on the owner when a type is turned back
        /// on (e.g. to act on it soon).
        /// </summary>
        public void Register(ZNetView view, Action onAllowed)
        {
            m_nview = view;
            m_onAllowed = onAllowed;
            view.Register<string, bool>(m_rpc, RPC_SetDenied);
        }

        /// <summary>Owner only, from WardBase.InitDefaults: stamps the config default list once per ward.</summary>
        public void InitDefaults(ZDO zdo, string defaultList)
        {
            if (zdo.GetBool(m_initKey))
                return;
            zdo.Set(m_key, NameLists.Join(NameLists.Parse(defaultList)));
            zdo.Set(m_initKey, true);
        }

        private bool IsValid => m_nview != null && m_nview.IsValid();

        /// <summary>The ZDO string the cache was last built from; changes whenever the list does.</summary>
        public string Source
        {
            get
            {
                Refresh();
                return m_source;
            }
        }

        /// <summary>Every denied prefab name.</summary>
        public IReadOnlyCollection<string> Denied
        {
            get
            {
                Refresh();
                return m_denied;
            }
        }

        public bool IsDenied(string prefab)
        {
            Refresh();
            return m_denied.Contains(prefab);
        }

        /// <summary>Asks the owner to deny or allow one prefab type. Safe to call from any client.</summary>
        public void RequestSet(string prefab, bool denied)
        {
            if (IsValid && !string.IsNullOrEmpty(prefab))
                m_nview.InvokeRPC(m_rpc, prefab, denied);
        }

        private void Refresh()
        {
            string source = IsValid ? m_nview.GetZDO().GetString(m_key) : "";
            if (source == m_source)
                return;
            m_source = source;
            m_denied.Clear();
            m_denied.UnionWith(NameLists.Parse(source));
        }

        private void RPC_SetDenied(long sender, string prefab, bool denied)
        {
            if (!m_nview.IsOwner() || string.IsNullOrEmpty(prefab))
                return;
            var names = new HashSet<string>(NameLists.Parse(m_nview.GetZDO().GetString(m_key)));
            bool changed = denied ? names.Add(prefab) : names.Remove(prefab);
            if (!changed)
                return;
            m_nview.GetZDO().Set(m_key, NameLists.Join(names));
            DebugLog.Write($"{DebugLog.Name("Ward", m_nview)}: {prefab} turned {(denied ? "off" : "on")} (by peer {sender}).");
            if (!denied)
                m_onAllowed?.Invoke();
        }
    }
}
