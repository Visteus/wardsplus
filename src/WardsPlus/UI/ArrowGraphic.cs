using UnityEngine;
using UnityEngine.UI;

namespace WardsPlus.UI
{
    /// <summary>
    /// A plain filled triangle pointing up or down. Drawn as a mesh rather than a "▲" character because
    /// Valheim's fonts don't include those glyphs. Maskable, so it clips inside scrolling lists.
    /// </summary>
    internal sealed class ArrowGraphic : MaskableGraphic
    {
        public bool PointsUp = true;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect rect = GetPixelAdjustedRect();
            float width = Mathf.Min(rect.width, rect.height) * 0.7f;
            float height = width * 0.6f;
            Vector2 center = rect.center;
            float tip = PointsUp ? height * 0.5f : -height * 0.5f;

            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;
            vertex.position = new Vector2(center.x - width * 0.5f, center.y - tip);
            vh.AddVert(vertex);
            vertex.position = new Vector2(center.x + width * 0.5f, center.y - tip);
            vh.AddVert(vertex);
            vertex.position = new Vector2(center.x, center.y + tip);
            vh.AddVert(vertex);
            vh.AddTriangle(0, 1, 2);
        }
    }
}
