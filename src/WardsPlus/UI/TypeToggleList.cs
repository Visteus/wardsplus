using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WardsPlus.Components;

namespace WardsPlus.UI
{
    /// <summary>
    /// A scrolling list with one checkbox row per prefab type ("Campfire ×5"), backed by a ward's
    /// <see cref="PrefabDenyList"/>: checked means allowed, and a click asks the ward's owner to change it.
    /// - Types not currently in range but denied are still listed (count 0), so they can be turned back on.
    /// - With a <see cref="PrefabPriorityList"/>, rows are shown in priority order (moved types first, then the
    ///   rest alphabetically) and get ▲/▼ buttons. Buttons rather than dragging, so it works on a gamepad
    ///   and never fights the list's own scrolling; focus follows the moved row so it can be pressed again.
    /// - Rows are rebuilt only when the set of types, their counts or the order actually changes, so a click
    ///   never lands on a row that is being replaced; checkbox states are synced every frame without notifying.
    /// Lives only while the ward's panel is open (the panel destroys its controls on close).
    /// </summary>
    internal sealed class TypeToggleList
    {
        /// <summary>Default visible rows before the list scrolls.</summary>
        public const int DefaultMaxRows = 7;

        private readonly RectTransform m_content;
        private readonly PrefabDenyList m_denyList;
        private readonly PrefabPriorityList m_priority;
        private readonly string m_emptyNoteToken;
        private readonly Func<string, string> m_nameForMissing;
        private readonly Dictionary<string, Toggle> m_toggles = new Dictionary<string, Toggle>();

        // What the rows were last built from.
        private SortedDictionary<string, int> m_counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<string, string> m_names = new Dictionary<string, string>();
        private string m_signature;
        private string m_priorityShown;

        /// <summary>After an arrow press: the row and arrow to give focus back to once the rows are rebuilt.</summary>
        private string m_focusPrefab;
        private bool m_focusUp;

        /// <param name="parent">Panel area to add the list to (a ward's PopulatePanel extras).</param>
        /// <param name="emptyNoteToken">Localization token shown when there is nothing to list.</param>
        /// <param name="priority">Optional: adds ▲/▼ buttons that edit this order.</param>
        /// <param name="nameForMissing">
        /// Localized name for a denied type that isn't in range (so no instance to read it from). Defaults to
        /// the prefab's Piece name.
        /// </param>
        public TypeToggleList(Transform parent, PrefabDenyList denyList, string emptyNoteToken,
            int maxRows = DefaultMaxRows, PrefabPriorityList priority = null, Func<string, string> nameForMissing = null)
        {
            m_content = WardPanel.AddScrollGroup(parent, maxRows).Content;
            m_denyList = denyList;
            m_priority = priority;
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
        /// Does nothing if the resulting types, counts and order are the same as last time.
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
            if (signature.ToString() == m_signature && PrioritySource == m_priorityShown)
                return;

            m_signature = signature.ToString();
            m_counts = counts;
            m_names = names;
            BuildRows();
        }

        /// <summary>
        /// Keeps the list in line with the synced ward (another player may change it): re-orders the rows if
        /// the priority order changed, and updates the checkboxes.
        /// </summary>
        public void SyncToggles()
        {
            if (!IsAlive)
                return;
            if (m_signature != null && PrioritySource != m_priorityShown)
                BuildRows();

            foreach (KeyValuePair<string, Toggle> pair in m_toggles)
            {
                bool on = !m_denyList.IsDenied(pair.Key);
                if (pair.Value && pair.Value.isOn != on)
                    pair.Value.SetIsOnWithoutNotify(on);
            }
        }

        private string PrioritySource => m_priority?.Source;

        private void BuildRows()
        {
            m_priorityShown = PrioritySource;
            WardPanel.ClearChildren(m_content);
            m_toggles.Clear();

            if (m_counts.Count == 0)
            {
                m_focusPrefab = null;
                WardPanel.AddNote(m_content, Localization.instance.Localize(m_emptyNoteToken));
                return;
            }

            var rows = new List<KeyValuePair<string, string>>(); // prefab, label
            foreach (KeyValuePair<string, int> pair in m_counts)
            {
                string label = m_names.TryGetValue(pair.Key, out string display) ? display : m_nameForMissing(pair.Key);
                if (pair.Value > 1)
                    label += $" ×{pair.Value}";
                rows.Add(new KeyValuePair<string, string>(pair.Key, label));
            }
            rows.Sort((a, b) =>
            {
                if (m_priority != null)
                {
                    int byRank = m_priority.RankOf(a.Key).CompareTo(m_priority.RankOf(b.Key));
                    if (byRank != 0)
                        return byRank;
                }
                return string.Compare(a.Value, b.Value, StringComparison.CurrentCultureIgnoreCase);
            });

            var order = new List<string>(rows.Count);
            foreach (KeyValuePair<string, string> row in rows)
                order.Add(row.Key);

            Button focus = null;
            for (int i = 0; i < rows.Count; i++)
            {
                string prefab = rows[i].Key;
                Toggle toggle = WardPanel.AddToggleRow(m_content, rows[i].Value, !m_denyList.IsDenied(prefab),
                    on => m_denyList.RequestSet(prefab, !on));
                m_toggles[prefab] = toggle;
                if (m_priority == null)
                    continue;

                // "▲▼ [x]" at the end of the row.
                int index = i;
                int at = toggle.transform.GetSiblingIndex();
                Transform arrows = WardPanel.AddArrowButtons(toggle.transform.parent,
                    () => MoveUp(order, index, prefab, focusUp: true),
                    () => MoveUp(order, index + 1, prefab, focusUp: false),
                    out Button up, out Button down);
                arrows.SetSiblingIndex(at);
                up.interactable = index > 0;
                down.interactable = index < rows.Count - 1;

                if (prefab == m_focusPrefab)
                {
                    focus = m_focusUp ? up : down;
                    if (!focus.interactable)
                        focus = m_focusUp ? down : up; // Reached the top/bottom: keep focus on the row.
                }
            }

            m_focusPrefab = null;
            if (focus && focus.interactable && EventSystem.current)
                EventSystem.current.SetSelectedGameObject(focus.gameObject);
        }

        /// <summary>
        /// Moves the row at <paramref name="index"/> above the one before it (▼ on a row is ▲ on the next one).
        /// Unranked rows shown above it are ranked in their shown order first, so nothing else jumps around.
        /// </summary>
        private void MoveUp(List<string> order, int index, string focusPrefab, bool focusUp)
        {
            if (index <= 0 || index >= order.Count)
                return;
            var rankFirst = new List<string>();
            for (int i = 0; i < index; i++)
            {
                if (!m_priority.IsRanked(order[i]))
                    rankFirst.Add(order[i]);
            }
            m_focusPrefab = focusPrefab;
            m_focusUp = focusUp;
            m_priority.RequestMoveAbove(order[index], order[index - 1], rankFirst);
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
