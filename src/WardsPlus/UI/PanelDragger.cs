using UnityEngine;
using UnityEngine.EventSystems;

namespace WardsPlus.UI
{
    /// <summary>
    /// Drag handler for UI panels that keeps the panel's real edges on screen.
    /// Replaces Jotunn's DragWindowCntrl, whose clamp assumes a centered pivot: with the ward panel's
    /// top-left pivot it stopped the left edge half a panel-width from the screen edge and let the
    /// panel slide half off the right and bottom.
    /// </summary>
    internal sealed class PanelDragger : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        private RectTransform _window;
        private Vector3 _grabOffset;

        private void Awake()
        {
            _window = (RectTransform)transform;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (ScreenToWorld(eventData.position, eventData.pressEventCamera, out Vector3 pointer))
                _grabOffset = _window.position - pointer;
        }

        public void OnDrag(PointerEventData eventData)
        {
            Camera cam = eventData.pressEventCamera;
            if (!ScreenToWorld(eventData.position, cam, out Vector3 pointer))
                return;

            _window.position = pointer + _grabOffset;
            KeepOnScreen(cam);
        }

        /// <summary>Nudges the panel so its full rect is inside the screen (top-left wins if it can't fit).</summary>
        private void KeepOnScreen(Camera cam)
        {
            var corners = new Vector3[4];
            _window.GetWorldCorners(corners);

            Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 max = min;
            for (int i = 1; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            Vector2 shift = Vector2.zero;
            if (max.x > Screen.width)
                shift.x = Screen.width - max.x;
            if (min.x + shift.x < 0f)
                shift.x = -min.x;
            if (min.y < 0f)
                shift.y = -min.y;
            if (max.y + shift.y > Screen.height)
                shift.y = Screen.height - max.y;

            if (shift == Vector2.zero)
                return;

            Vector2 pivotScreen = RectTransformUtility.WorldToScreenPoint(cam, _window.position);
            if (ScreenToWorld(pivotScreen + shift, cam, out Vector3 moved))
                _window.position = moved;
        }

        private bool ScreenToWorld(Vector2 screen, Camera cam, out Vector3 world)
        {
            var space = _window.parent as RectTransform;
            if (!space)
            {
                world = screen;
                return true;
            }
            return RectTransformUtility.ScreenPointToWorldPointInRectangle(space, screen, cam, out world);
        }
    }
}
