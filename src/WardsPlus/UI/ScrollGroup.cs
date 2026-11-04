using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace WardsPlus.UI
{
    /// <summary>
    /// A vertical list that grows with its rows up to a maximum height, then scrolls (mouse wheel or the
    /// Valheim-styled scrollbar, which only shows when needed). Fill <see cref="Content"/>; the height follows
    /// on its own, so a short list looks exactly like a plain group.
    /// </summary>
    internal sealed class ScrollGroup : MonoBehaviour
    {
        private const float ScrollbarWidth = 10f;
        private const float ScrollbarGap = 6f;

        /// <summary>Where rows go. Laid out vertically and sized to its rows.</summary>
        public RectTransform Content { get; private set; }

        private LayoutElement _layout;
        private float _maxHeight;

        public static ScrollGroup Create(Transform parent, float maxHeight)
        {
            var root = new GameObject("ScrollGroup", typeof(RectTransform));
            root.transform.SetParent(parent, false);

            // Transparent graphic so the wheel scrolls anywhere over the list, not just over a row.
            var hitArea = root.AddComponent<Image>();
            hitArea.color = Color.clear;
            root.AddComponent<RectMask2D>();

            var group = root.AddComponent<ScrollGroup>();
            group._maxHeight = maxHeight;
            group._layout = root.AddComponent<LayoutElement>();
            group._layout.preferredHeight = 0f;

            // Content: top-anchored, full width minus room for the scrollbar, height from its rows.
            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(root.transform, false);
            var content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(-(ScrollbarWidth + ScrollbarGap), 0f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            group.Content = content;

            // Scrollbar along the right edge.
            GUIManager ui = GUIManager.Instance;
            GameObject scrollbarGo = DefaultControls.CreateScrollbar(ui.ValheimControlResources);
            scrollbarGo.transform.SetParent(root.transform, false);
            var scrollbarRect = (RectTransform)scrollbarGo.transform;
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.sizeDelta = new Vector2(ScrollbarWidth, 0f);
            scrollbarRect.anchoredPosition = Vector2.zero;
            var scrollbar = scrollbarGo.GetComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            ui.ApplyScrollbarStyle(scrollbar);

            var scroll = root.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = (RectTransform)root.transform;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = 30f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            return group;
        }

        // Follows the rows' height (computed by the content's size fitter during layout), capped at the max.
        private void LateUpdate()
        {
            if (!Content)
                return;
            float height = Mathf.Min(Content.rect.height, _maxHeight);
            if (Mathf.Abs(_layout.preferredHeight - height) < 0.5f)
                return;
            _layout.preferredHeight = height;
            if (transform.parent is RectTransform parent)
                LayoutRebuilder.MarkLayoutForRebuild(parent);
        }
    }
}
