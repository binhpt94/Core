using System;
using System.Collections.Generic;
using Game.Core.DevTools;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// Lays ONE view out on ONE device without a scene, a game or Play Mode: instantiate → world-space canvas of
    /// the device's canvas size → apply fixture → force layout → probe. Used by the layout gate and the preview window.
    /// </summary>
    public sealed class ViewStage : IDisposable
    {
        public const string HostName = "~ViewStage";

        private readonly GameObject _host;

        public LayoutCase Layout { get; }
        public GameObject Root { get; }

        private ViewStage(GameObject host, GameObject root, LayoutCase layout)
        {
            _host = host;
            Root = root;
            Layout = layout;
        }

        public static ViewStage Open(IViewCases cases, string fixture, DeviceProfile device, bool hidden = true)
        {
            var host = new GameObject(HostName);
            if (hidden)
                host.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                object view = cases.Create(host.transform, out var root);
                var canvas = root.GetComponentInChildren<Canvas>(true);
                if (canvas == null)
                    throw new InvalidOperationException(cases.ViewId + ": the view has no Canvas");

                // Same canvas size the CanvasScaler would give on that device; then lay out in world space.
                var scaler = canvas.GetComponent<CanvasScaler>();
                int refW = scaler != null ? Mathf.RoundToInt(scaler.referenceResolution.x) : device.Width;
                int refH = scaler != null ? Mathf.RoundToInt(scaler.referenceResolution.y) : device.Height;
                double match = scaler != null ? scaler.matchWidthOrHeight : 0;
                var size = device.Canvas(refW, refH, match);
                if (scaler != null)
                    scaler.enabled = false;
                canvas.renderMode = RenderMode.WorldSpace;
                var rt = (RectTransform)canvas.transform;
                rt.localScale = Vector3.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(size.W, size.H);

                cases.Apply(view, fixture);
                ForceLayout(rt);

                var art = ArtSlotLabels(cases, root, rt);
                var tree = LayoutProbe.Capture(rt, art, out var artNodes);
                var layout = new LayoutCase(fixture, device.Name, size, device.SafeArea(refW, refH, match), tree, artNodes);
                return new ViewStage(host, root, layout);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(host);
                throw;
            }
        }

        /// <summary>The view's declared art slot objects → "pivot=x,y art:&lt;slot&gt;" (canvas units, y down).</summary>
        private static Dictionary<RectTransform, string> ArtSlotLabels(IViewCases cases, GameObject root, RectTransform canvas)
        {
            var labels = new Dictionary<RectTransform, string>();
            foreach (var p in cases.ArtPlacements)
                foreach (var t in p.Find(root))
                {
                    Vector3 at = canvas.InverseTransformPoint(t.position);
                    var c = canvas.rect;
                    labels[t] = "pivot=" + Mathf.RoundToInt(at.x - c.xMin) + "," + Mathf.RoundToInt(c.yMax - at.y) + " art:" + p.SlotId;
                }
            return labels;
        }

        private static void ForceLayout(RectTransform root)
        {
            // Nested layout groups need more than one pass to settle.
            for (int i = 0; i < 3; i++)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            }
        }

        public List<LayoutViolation> Check(LayoutLimits limits) => LayoutChecks.Check(Layout.Root, Layout.SafeArea, limits);

        public void Dispose()
        {
            if (_host != null)
                UnityEngine.Object.DestroyImmediate(_host);
        }
    }

    /// <summary>RectTransform tree → engine-free <see cref="LayoutNode"/> tree (active objects only, canvas units, y down).</summary>
    public static class LayoutProbe
    {
        public static LayoutNode Capture(RectTransform canvas) =>
            Capture(canvas, new Dictionary<RectTransform, string>(), out _);

        /// <summary>Also maps the given art slot objects to their nodes (labels for the snapshot).</summary>
        public static LayoutNode Capture(RectTransform canvas, IReadOnlyDictionary<RectTransform, string> art, out Dictionary<LayoutNode, string> artNodes)
        {
            var root = new LayoutNode("Canvas", LayoutNodeKind.Container, new LayoutRect(0, 0, Mathf.RoundToInt(canvas.rect.width), Mathf.RoundToInt(canvas.rect.height)));
            artNodes = new Dictionary<LayoutNode, string>();
            AddChildren(canvas, canvas, root, art, artNodes);
            return root;
        }

        private static void AddChildren(RectTransform canvas, Transform parent, LayoutNode into,
            IReadOnlyDictionary<RectTransform, string> art, Dictionary<LayoutNode, string> artNodes)
        {
            var seen = new Dictionary<string, int>();
            foreach (Transform child in parent)
            {
                if (!child.gameObject.activeInHierarchy || !(child is RectTransform rt))
                    continue;
                string name = child.name.Replace(' ', '_');
                seen.TryGetValue(name, out int n);
                seen[name] = n + 1;
                if (n > 0)
                    name += "#" + (n + 1); // duplicate sibling names stay distinguishable in the snapshot

                var rect = RectIn(canvas, rt);
                var text = child.GetComponent<Text>();
                LayoutNode node;
                if (child.GetComponent<Selectable>() is Selectable s && s.enabled)
                    node = new LayoutNode(name, LayoutNodeKind.Interactive, rect);
                else if (text != null && text.enabled)
                    node = new LayoutNode(name, LayoutNodeKind.Text, rect, NeededHeight(text, rt), string.IsNullOrEmpty(text.text));
                else if (child.GetComponent<Graphic>() is Graphic g && g.enabled)
                    node = new LayoutNode(name, LayoutNodeKind.Decoration, rect);
                else
                    node = new LayoutNode(name, LayoutNodeKind.Container, rect);

                into.Add(node);
                if (art.TryGetValue(rt, out var label))
                    artNodes[node] = label;
                AddChildren(canvas, child, node, art, artNodes);
            }
        }

        private static LayoutRect RectIn(RectTransform canvas, RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 min = canvas.InverseTransformPoint(corners[0]); // bottom-left
            Vector3 max = canvas.InverseTransformPoint(corners[2]); // top-right
            var c = canvas.rect;
            int x = Mathf.RoundToInt(min.x - c.xMin);
            int y = Mathf.RoundToInt(c.yMax - max.y);
            return new LayoutRect(x, y, Mathf.RoundToInt(max.x - min.x), Mathf.RoundToInt(max.y - min.y));
        }

        /// <summary>Height the text needs at its width; with best fit, at the MINIMUM size (if that fits, best fit will find a size).</summary>
        private static int NeededHeight(Text text, RectTransform rt)
        {
            if (string.IsNullOrEmpty(text.text) || text.font == null)
                return -1;
            var settings = text.GetGenerationSettings(new Vector2(rt.rect.width, 0f));
            if (text.resizeTextForBestFit)
            {
                settings.resizeTextForBestFit = false;
                settings.fontSize = text.resizeTextMinSize;
            }
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            float h = text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / Mathf.Max(0.0001f, text.pixelsPerUnit);
            return Mathf.CeilToInt(h);
        }
    }
}
