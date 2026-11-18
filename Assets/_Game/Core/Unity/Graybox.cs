using UnityEngine;
using UnityEngine.UI;

namespace Game.Core.Presentation
{
    /// <summary>
    /// Builds the placeholder of a slot FROM ITS CONTRACT (centre of the size envelope, exact pivot), so gameplay, layout
    /// tests and the art gate run before any art exists, and the graybox can never violate what real art must meet.
    /// Visibly a placeholder (hatched colour + slot id), so nobody mistakes it for final art.
    /// </summary>
    public static class Graybox
    {
        public static readonly Color Fill = new Color(1f, 0.35f, 0.85f, 0.85f);

        /// <summary>uGUI placeholder for UiImage slots (other kinds: same idea with a quad/cube; not needed by the sample).</summary>
        public static RectTransform CreateUi(ArtSlotContract contract, Transform parent)
        {
            var m = ArtChecks.Graybox(contract);
            var go = new GameObject("Graybox " + contract.Id, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            float px = m.PivotXPercent / 100f, py = m.PivotYPercent / 100f;
            rt.pivot = new Vector2(px, py); // layout: dynamic
            rt.sizeDelta = new Vector2(m.Width, m.Height); // layout: dynamic
            var image = go.GetComponent<Image>();
            image.color = Fill;
            image.raycastTarget = false;
            return rt;
        }

        /// <summary>Instantiates the slot's best bound art, or its graybox. Level tells the caller what it got.</summary>
        public static RectTransform InstantiateUi(ArtSlotContract contract, ArtSlotAsset binding, Transform parent, out ArtLevel level)
        {
            level = ArtLevel.Graybox;
            var art = binding != null ? binding.Resolve(out level) : null;
            if (art == null)
                return CreateUi(contract, parent);
            var instance = Object.Instantiate(art, parent, false);
            return (RectTransform)instance.transform;
        }
    }
}
