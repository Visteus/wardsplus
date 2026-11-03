using Jotunn.Managers;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WardsPlus.Components;
using WardsPlus.Config;
using WardsPlus.Util;

namespace WardsPlus.UI
{
    /// <summary>
    /// Per-ward settings panel that lives on the vanilla inventory screen, next to the container panel
    /// (same approach as ItemHopper/ValheimPipes). It is shown whenever the inventory screen is open for a
    /// ward, so vanilla handles closing (Esc, Tab, E, walking away) and input/cursor locking.
    /// Driven every frame by <see cref="Patches.InventoryGuiPatches"/>.
    /// </summary>
    internal static class WardPanel
    {
        private const float Width = 320f;
        private const float Gap = 12f;
        private const float RangeSendInterval = 0.2f;
        private const float LocalEditGrace = 0.5f;
        private static readonly Color LabelColor = new Color(0.86f, 0.86f, 0.86f);

        /// <summary>Ward opened without a container (Spawnproof); null otherwise.</summary>
        private static WardBase s_noContainerWard;

        private static GameObject s_root;
        private static Canvas s_canvas;
        /// <summary>True while the panel is layered below the inventory screen for a vanilla pop-up.</summary>
        private static bool s_sortedBelow;
        private static Text s_title;
        private static Toggle s_enabled;
        private static Slider s_range;
        private static Text s_rangeValue;
        private static RectTransform s_extras;

        private static WardBase s_ward;
        private static uint s_lastRevision;
        private static float s_pendingRange = -1f;
        private static float s_nextRangeSend;
        private static float s_lastLocalEdit = -100f;
        /// <summary>True while the panel itself is changing controls; their callbacks must not send RPCs.</summary>
        private static bool s_applyingState;

        /// <summary>
        /// Where the panel was last placed by default, for which ward type, and how far the player has
        /// dragged it from the default spot, per ward type (types use different default spots).
        /// </summary>
        private static Vector3 s_defaultPosition;
        private static WardType s_placedType;
        private static bool s_placed;
        private static readonly Dictionary<WardType, Vector3> s_dragOffsets = new Dictionary<WardType, Vector3>();

        public static bool IsOpen => s_root && s_root.activeSelf;

        /// <summary>Opens the inventory screen with no container and shows this ward's panel in its place.</summary>
        public static void OpenWithoutContainer(WardBase ward)
        {
            InventoryGui gui = InventoryGui.instance;
            if (!gui)
                return;
            s_noContainerWard = ward;
            gui.Show(null);
        }

        /// <summary>Called after every InventoryGui.Update.</summary>
        public static void Tick(InventoryGui gui)
        {
            WardBase ward = ResolveWard(gui);
            if (!ward)
            {
                Hide();
                return;
            }

            EnsureCreated(gui);
            if (!s_root)
                return;

            if (ward != s_ward)
                Bind(gui, ward);

            if (!s_root.activeSelf)
            {
                s_root.SetActive(true);
                UpdateSorting(gui, force: true);
            }
            else
            {
                UpdateSorting(gui, force: false);
            }

            ward.ShowAreaMarker();
            FlushPendingRange(force: false);
            if (ward.DataRevision != s_lastRevision)
                SyncFromWard();
            ward.RefreshPanel();
        }

        /// <summary>Which ward (if any) the inventory screen is currently open for.</summary>
        private static WardBase ResolveWard(InventoryGui gui)
        {
            Player player = Player.m_localPlayer;
            if (!gui || !player || !InventoryGui.IsVisible())
            {
                s_noContainerWard = null;
                return null;
            }

            if (gui.m_currentContainer)
            {
                s_noContainerWard = null;
                return gui.m_currentContainer.GetComponentInParent<WardBase>();
            }

            if (s_noContainerWard)
            {
                // Mirror vanilla's container auto-close: drop the panel when the ward is gone or out of reach.
                bool inReach = s_noContainerWard.IsValid &&
                    Vector3.Distance(s_noContainerWard.transform.position, player.transform.position) <= gui.m_autoCloseDistance;
                if (inReach)
                    return s_noContainerWard;
                s_noContainerWard = null;
            }
            return null;
        }

        private static void Bind(InventoryGui gui, WardBase ward)
        {
            // Finish any edit for the previous ward before switching, then start clean for this one.
            FlushPendingRange(force: true);
            ClearExtras();
            s_pendingRange = -1f;
            s_lastLocalEdit = -100f;

            CaptureDragOffset();
            PlacePanel(gui, ward);

            s_ward = ward;
            s_title.text = Localization.instance.Localize(ward.m_name);

            // Changing min/max makes Unity clamp the slider's current value and fire onValueChanged,
            // which must not be mistaken for the player dragging it (it would overwrite the ward's range).
            s_applyingState = true;
            try
            {
                s_range.minValue = Mathf.Min(ward.Config.MinRange.Value, ward.Config.MaxRange.Value);
                s_range.maxValue = Mathf.Max(ward.Config.MinRange.Value, ward.Config.MaxRange.Value);
            }
            finally
            {
                s_applyingState = false;
            }
            SyncFromWard();

            ward.PopulatePanel(s_extras);
            s_extras.gameObject.SetActive(s_extras.childCount > 0);
        }

        private static void Hide()
        {
            if (s_ward)
                FlushPendingRange(force: true);
            s_ward = null;
            s_pendingRange = -1f;
            if (s_root && s_root.activeSelf)
            {
                CaptureDragOffset();
                ClearExtras();
                s_root.SetActive(false);
            }
        }

        /// <summary>Pulls the current synced values into the controls (another player may have changed them).</summary>
        private static void SyncFromWard()
        {
            s_lastRevision = s_ward.DataRevision;
            s_enabled.SetIsOnWithoutNotify(s_ward.IsEnabled());

            // Don't yank the slider out from under a drag that hasn't round-tripped yet.
            if (s_pendingRange < 0f && Time.time - s_lastLocalEdit > LocalEditGrace)
            {
                float range = s_ward.GetRange();
                s_range.SetValueWithoutNotify(range);
                s_rangeValue.text = $"{range:0}m";
            }
        }

        // Controls only ever send values that came from the player. Anything the panel sets itself
        // (binding a ward, syncing another player's change) must not echo back as a write.
        private static void OnEnabledChanged(bool value)
        {
            if (s_applyingState || !s_ward)
                return;
            s_ward.RequestSetBool(ZdoKeys.Enabled, value);
        }

        private static void OnRangeChanged(float value)
        {
            if (s_applyingState || !s_ward)
                return;
            s_rangeValue.text = $"{value:0}m";
            s_pendingRange = value;
            s_lastLocalEdit = Time.time;
        }

        /// <summary>Rate-limits range RPCs while the slider is dragged.</summary>
        private static void FlushPendingRange(bool force)
        {
            if (s_pendingRange < 0f || !s_ward)
                return;
            if (!force && Time.time < s_nextRangeSend)
                return;
            s_ward.RequestSetFloat(ZdoKeys.Range, s_pendingRange);
            s_pendingRange = -1f;
            s_nextRangeSend = Time.time + RangeSendInterval;
        }

        private static void ClearExtras()
        {
            if (!s_extras)
                return;
            for (int i = s_extras.childCount - 1; i >= 0; i--)
                Object.Destroy(s_extras.GetChild(i).gameObject);
        }

        // ---------------------------------------------------------------- construction

        private static void EnsureCreated(InventoryGui gui)
        {
            if (s_root)
                return; // Unity-null after a logout destroys InventoryGui; rebuilt on the next open.
            if (GUIManager.IsHeadless() || !gui.m_container)
                return;

            RectTransform anchor = gui.m_container;
            GUIManager ui = GUIManager.Instance;

            s_root = ui.CreateWoodpanel(anchor.parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Width, 200f, draggable: false);
            s_root.name = "WardsPlus_WardPanel";
            // Top-left pivot: the panel hangs below whatever it is placed under and grows downward, so
            // ward-specific controls never push it up over the inventory. Positioned per open in PlacePanel.
            var rect = (RectTransform)s_root.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            s_placed = false;

            var layout = s_root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 20);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            s_root.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // Our own dragger: Jotunn's DragWindowCntrl clamps as if the pivot were centered.
            s_root.AddComponent<PanelDragger>();

            // Own canvas so the panel can draw above the crafting panel and menu bar; it needs its own
            // raycaster to keep receiving clicks. Sorting is applied in UpdateSorting.
            s_canvas = s_root.AddComponent<Canvas>();
            s_root.AddComponent<GraphicRaycaster>();

            // Title
            s_title = CreateLabel(s_root.transform, "", ui.NorseBold, 28, GUIManager.Instance.ValheimOrange, 36f);
            s_title.alignment = TextAnchor.MiddleCenter;

            // Enabled row
            Transform enabledRow = CreateRow(s_root.transform, 30f);
            Text enabledLabel = CreateLabel(enabledRow, Localization.instance.Localize("$wp_panel_enabled"), ui.AveriaSerifBold, 18, LabelColor, 30f);
            enabledLabel.GetComponent<LayoutElement>().flexibleWidth = 1f;
            GameObject toggleGo = ui.CreateToggle(enabledRow, 28f, 28f);
            Transform defaultLabel = toggleGo.transform.Find("Label");
            if (defaultLabel)
                Object.Destroy(defaultLabel.gameObject);
            AddLayout(toggleGo, 28f, 28f);
            s_enabled = toggleGo.GetComponent<Toggle>();
            s_enabled.onValueChanged.AddListener(OnEnabledChanged);

            // Range header row
            Transform rangeRow = CreateRow(s_root.transform, 26f);
            Text rangeLabel = CreateLabel(rangeRow, Localization.instance.Localize("$wp_range"), ui.AveriaSerifBold, 18, LabelColor, 26f);
            rangeLabel.GetComponent<LayoutElement>().flexibleWidth = 1f;
            s_rangeValue = CreateLabel(rangeRow, "", ui.AveriaSerifBold, 18, GUIManager.Instance.ValheimYellow, 26f);
            s_rangeValue.alignment = TextAnchor.MiddleRight;
            s_rangeValue.GetComponent<LayoutElement>().preferredWidth = 70f;

            // Range slider
            GameObject sliderGo = DefaultControls.CreateSlider(ui.ValheimControlResources);
            sliderGo.transform.SetParent(s_root.transform, false);
            s_range = sliderGo.GetComponent<Slider>();
            ui.ApplySliderStyle(s_range);
            s_range.wholeNumbers = true;
            s_range.onValueChanged.AddListener(OnRangeChanged);
            AddLayout(sliderGo, -1f, 20f);

            // Ward-specific controls go here (filled by WardBase.PopulatePanel).
            var extrasGo = new GameObject("Extras", typeof(RectTransform));
            extrasGo.transform.SetParent(s_root.transform, false);
            var extrasLayout = extrasGo.AddComponent<VerticalLayoutGroup>();
            extrasLayout.spacing = 6f;
            extrasLayout.childControlWidth = true;
            extrasLayout.childControlHeight = true;
            extrasLayout.childForceExpandWidth = true;
            extrasLayout.childForceExpandHeight = false;
            s_extras = (RectTransform)extrasGo.transform;
            extrasGo.SetActive(false);

            s_root.SetActive(false);
        }

        /// <summary>
        /// Default spot, left edges aligned:
        /// - wards with an inventory: directly below the container panel;
        /// - wards without one (Spawnproof): directly below the player inventory, where the container would be.
        /// The distance the player dragged the panel is remembered per ward type and kept on top of that
        /// type's default, so a drag that suits one layout never pushes another type's panel off screen.
        /// </summary>
        private static void PlacePanel(InventoryGui gui, WardBase ward)
        {
            var panel = (RectTransform)s_root.transform;
            RectTransform anchor = ward.m_container ? gui.m_container : gui.m_player;
            if (!anchor)
                anchor = gui.m_container;

            s_defaultPosition = BottomLeftInParentSpace(anchor, panel.parent) + new Vector3(0f, -Gap, 0f);
            s_dragOffsets.TryGetValue(ward.Type, out Vector3 offset);
            panel.localPosition = s_defaultPosition + offset;
            s_placedType = ward.Type;
            s_placed = true;
        }

        /// <summary>
        /// Chooses the panel's drawing layer:
        /// - normally one sorting step above everything on the inventory screen (its root canvas and any
        ///   sub-canvases), so the crafting panel and menu bar can never cover it and make it unreachable;
        /// - while a vanilla pop-up from the inventory screen is open (Compendium, skills, trophies, ...),
        ///   one step below the inventory screen, so the pop-up draws over it and gets every click.
        /// Only the layer number changes when switching, unless <paramref name="force"/> is set (on every
        /// open, because Unity can drop overrideSorting when a nested canvas is deactivated and reactivated).
        /// </summary>
        private static void UpdateSorting(InventoryGui gui, bool force)
        {
            if (!s_canvas)
                return;

            bool below = IsVanillaDialogOpen(gui);
            if (!force && below == s_sortedBelow)
                return;
            s_sortedBelow = below;

            Canvas root = s_canvas.transform.parent ? s_canvas.transform.parent.GetComponentInParent<Canvas>() : null;
            if (root)
                root = root.rootCanvas;
            int rootOrder = root ? root.sortingOrder : 0;

            int order;
            if (below)
            {
                order = rootOrder - 1;
            }
            else
            {
                int highest = rootOrder;
                foreach (Canvas canvas in gui.GetComponentsInChildren<Canvas>(true))
                {
                    if (canvas != s_canvas && canvas.overrideSorting)
                        highest = Mathf.Max(highest, canvas.sortingOrder);
                }
                order = highest + 1;
            }

            s_canvas.overrideSorting = true;
            if (root)
                s_canvas.sortingLayerID = root.sortingLayerID;
            s_canvas.sortingOrder = order;
        }

        /// <summary>
        /// The pop-ups the inventory screen can open on top of itself; the same set InventoryGui.Update
        /// checks before letting Escape close the inventory.
        /// </summary>
        private static bool IsVanillaDialogOpen(InventoryGui gui)
        {
            return (gui.m_textsDialog && gui.m_textsDialog.gameObject.activeSelf)
                || (gui.m_skillsDialog && gui.m_skillsDialog.gameObject.activeSelf)
                || (gui.m_trophiesPanel && gui.m_trophiesPanel.activeSelf)
                || (gui.m_achievementsPanel && gui.m_achievementsPanel.gameObject.activeSelf)
                || (gui.m_splitDialog && gui.m_splitDialog.IsActive)
                || (gui.m_variantDialog && gui.m_variantDialog.gameObject.activeSelf);
        }

        /// <summary>Remembers how far the player has dragged the panel from the open ward type's default spot.</summary>
        private static void CaptureDragOffset()
        {
            if (!PluginConfig.RememberPanelPosition.Value)
            {
                s_dragOffsets.Clear();
                return;
            }
            if (s_placed && s_root && s_root.activeSelf)
                s_dragOffsets[s_placedType] = s_root.transform.localPosition - s_defaultPosition;
        }

        /// <summary>
        /// Hooked to the "Remember Panel Position" setting. Turning it off forgets every remembered drag
        /// (so off-then-on acts as a reset) and snaps an open panel back to its default spot.
        /// </summary>
        public static void OnRememberPositionChanged()
        {
            if (PluginConfig.RememberPanelPosition.Value)
                return;
            s_dragOffsets.Clear();
            if (s_placed && s_root)
                s_root.transform.localPosition = s_defaultPosition;
        }

        /// <summary>
        /// Bottom-left corner of <paramref name="rect"/> expressed in <paramref name="space"/>'s local
        /// coordinates. Going through world space keeps it correct even if the two have different parents,
        /// and both live under the animated inventory root, so the result doesn't depend on animation state.
        /// </summary>
        private static Vector3 BottomLeftInParentSpace(RectTransform rect, Transform space)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners); // 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
            return space.InverseTransformPoint(corners[0]);
        }

        private static Transform CreateRow(Transform parent, float height)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            AddLayout(row, -1f, height);
            return row.transform;
        }

        private static Text CreateLabel(Transform parent, string text, Font font, int size, Color color, float height)
        {
            GameObject go = GUIManager.Instance.CreateText(text, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, font, size, color, true, Color.black, 100f, height, false);
            var label = go.GetComponent<Text>();
            label.alignment = TextAnchor.MiddleLeft;
            AddLayout(go, -1f, height);
            return label;
        }

        private static LayoutElement AddLayout(GameObject go, float width, float height)
        {
            var element = go.GetComponent<LayoutElement>();
            if (!element)
                element = go.AddComponent<LayoutElement>();
            if (width >= 0f)
                element.preferredWidth = width;
            if (height >= 0f)
                element.preferredHeight = height;
            return element;
        }
    }
}
