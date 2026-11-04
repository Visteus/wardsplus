using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using WardsPlus.Components;

namespace WardsPlus.UI
{
    /// <summary>
    /// A scrolling list with one checkbox row per prefab type ("Campfire ×5"), backed by a ward's
    /// <see cref="PrefabDenyList"/>: checked means allowed, and a click asks the ward's owner to change it.
    /// - Types not currently in range but denied are still listed (count 0), so they can be turned back on.
    /// - Rows are rebuilt only when the set of types or counts actually changes, so a click never lands on a
    ///   row that is being replaced; checkbox states are synced every frame without notifying.
    /// Lives only while the ward's panel is open (the panel destroys its controls on close).
    /// </summary>
    internal sealed class TypeToggleList
    {
        /// <summary>Default visible rows before the list scrolls.</summary>
        public const int DefaultMaxRows = 7;

        private readonly RectTransform m_content;
        private readonly PrefabDenyList m_denyList;
        private readonly string m_emptyNoteToken;
        private readonly Func<string, string> m_nameForMissing;
        private readonly Dictionary<string, Toggle> m_toggles = new Dictionary<string, Toggle>();
        private string m_signature;

        /// <param name="parent">Panel area to add the list to (a ward's PopulatePanel extras).</param>
        /// <param name="emptyNoteToken">Localization token shown when there is nothing to list.</param>
        /// <param name="nameForMissing">
        /// Localized name for a denied type that isn't in range (so no instance to read it from). Defaults to
        /// the prefab's Piece name.
        /// </param>
        public TypeToggleList(Transform parent, PrefabDenyList denyList, string emptyNoteToken,
            int maxRows = DefaultMaxRows, Func<string, string> nameForMissing = null)
        {
            m_content = WardPanel.AddScrollGroup(parent, maxRows).Content;
            m_denyList = denyList;
            m_emptyNoteToken = emptyNoteToken;
            m_nameForMissing = nameForMissing ?? PieceDisplayName;
        }

        /// <summary>False once the panel has closed and destroyed the list.</summary>
        public bool IsAlive => m_content;

        /// <summary>Shows or hides the whole list (e.g. while the feature it configures is turned off).</summary>
        public void SetVisible(bool visible)
        {
            Transform root = m_content ? m_content.parent : null;
            if (root && root.gameObject.activeSelf != visible)
                root.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Rebuilds the rows from the instances in range: one (prefab, localized name) pair per instance.
        /// Does nothing if the resulting types and counts are the same as last time.
        /// </summary>
        public void Rebuild(IEnumerable<KeyValuePair<string, string>> instances)
        {
            if (!IsAlive)
                return;

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> instance in instances)
            {
                counts.TryGetValue(instance.Key, out int count);
                counts[instance.Key] = count + 1;
                if (!names.ContainsKey(instance.Key))
                    names[instance.Key] = instance.Value;
            }
            foreach (string prefab in m_denyList.Denied)
            {
                if (!counts.ContainsKey(prefab))
                    counts[prefab] = 0;
            }

            var signature = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in counts)
                signature.Append(pair.Key).Append(':').Append(pair.Value).Append(';');
            if (signature.ToString() == m_signature)
                return;
            m_signature = signature.ToString();

            WardPanel.ClearChildren(m_content);
            m_toggles.Clear();

            if (counts.Count == 0)
            {
                WardPanel.AddNote(m_content, Localization.instance.Localize(m_emptyNoteToken));
                return;
            }

            var rows = new List<KeyValuePair<string, string>>(); // prefab, label
            foreach (KeyValuePair<string, int> pair in counts)
            {
                string label = names.TryGetValue(pair.Key, out string display) ? display : m_nameForMissing(pair.Key);
                if (pair.Value > 1)
                    label += $" ×{pair.Value}";
                rows.Add(new KeyValuePair<string, string>(pair.Key, label));
            }
            rows.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.CurrentCultureIgnoreCase));

            foreach (KeyValuePair<string, string> row in rows)
            {
                string prefab = row.Key;
                m_toggles[prefab] = WardPanel.AddToggleRow(m_content, row.Value, !m_denyList.IsDenied(prefab),
                    on => m_denyList.RequestSet(prefab, !on));
            }
        }

        /// <summary>Keeps the checkboxes in line with the synced deny list (another player may change it).</summary>
        public void SyncToggles()
        {
            foreach (KeyValuePair<string, Toggle> pair in m_toggles)
            {
                bool on = !m_denyList.IsDenied(pair.Key);
                if (pair.Value && pair.Value.isOn != on)
                    pair.Value.SetIsOnWithoutNotify(on);
            }
        }

        /// <summary>Localized Piece name of a prefab (from the prefab itself), or the prefab name.</summary>
        public static string PieceDisplayName(string prefab)
        {
            GameObject go = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefab) : null;
            Piece piece = go ? go.GetComponent<Piece>() : null;
            return piece ? Localization.instance.Localize(piece.m_name) : prefab;
        }
    }
}
