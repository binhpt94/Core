using System;
using System.Collections.Generic;

namespace Game.Core.DevTools
{
    /// <summary>Axis-aligned rectangle in canvas units, origin top-left, y down (like reading a screen).</summary>
    public readonly struct LayoutRect : IEquatable<LayoutRect>
    {
        public int X { get; }
        public int Y { get; }
        public int W { get; }
        public int H { get; }
        public int Right => X + W;
        public int Bottom => Y + H;

        public LayoutRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public bool IsEmpty => W <= 0 || H <= 0;

        public bool Contains(LayoutRect r, int tolerance = 0) =>
            r.X >= X - tolerance && r.Y >= Y - tolerance && r.Right <= Right + tolerance && r.Bottom <= Bottom + tolerance;

        /// <summary>Intersection, or an empty rect.</summary>
        public LayoutRect Intersect(LayoutRect r)
        {
            int x = Math.Max(X, r.X), y = Math.Max(Y, r.Y);
            int w = Math.Min(Right, r.Right) - x, h = Math.Min(Bottom, r.Bottom) - y;
            return w > 0 && h > 0 ? new LayoutRect(x, y, w, h) : new LayoutRect(x, y, 0, 0);
        }

        public bool Equals(LayoutRect o) => X == o.X && Y == o.Y && W == o.W && H == o.H;
        public override bool Equals(object obj) => obj is LayoutRect o && Equals(o);
        public override int GetHashCode() => ((X * 397 ^ Y) * 397 ^ W) * 397 ^ H;
        public override string ToString() => X + "," + Y + " " + W + "x" + H;
    }

    public enum LayoutNodeKind
    {
        /// <summary>Only groups children (no graphic).</summary>
        Container,
        /// <summary>Image/background that may bleed outside the safe area (not content).</summary>
        Decoration,
        Text,
        /// <summary>Button, toggle, slider... (a touch target).</summary>
        Interactive
    }

    /// <summary>
    /// Engine-free picture of a laid-out UI tree. The Unity probe (Core/Editor) fills it from RectTransforms;
    /// everything that judges it (checks, snapshot, diff) is pure and runs in dotnet test.
    /// </summary>
    public sealed class LayoutNode
    {
        private readonly List<LayoutNode> _children = new List<LayoutNode>();

        public string Name { get; }
        public LayoutNodeKind Kind { get; }
        public LayoutRect Rect { get; }

        /// <summary>Text only: height the text needs at its (minimum best-fit) font size; -1 = unknown.</summary>
        public int NeededHeight { get; }

        /// <summary>Text only: true if the string is empty (nothing to clip or overlap).</summary>
        public bool IsEmptyText { get; }

        public LayoutNode Parent { get; private set; }
        public IReadOnlyList<LayoutNode> Children => _children;

        public LayoutNode(string name, LayoutNodeKind kind, LayoutRect rect, int neededHeight = -1, bool isEmptyText = false)
        {
            Name = name;
            Kind = kind;
            Rect = rect;
            NeededHeight = neededHeight;
            IsEmptyText = isEmptyText;
        }

        public LayoutNode Add(LayoutNode child)
        {
            child.Parent = this;
            _children.Add(child);
            return this;
        }

        /// <summary>Path below the root, e.g. "Background/Options/Option0" (the root's own name is not included).</summary>
        public string Path => Parent == null ? "" : Parent.Parent == null ? Name : Parent.Path + "/" + Name;

        public bool IsAncestorOf(LayoutNode other)
        {
            for (var p = other.Parent; p != null; p = p.Parent)
                if (p == this)
                    return true;
            return false;
        }

        public IEnumerable<LayoutNode> Descendants()
        {
            foreach (var c in _children)
            {
                yield return c;
                foreach (var d in c.Descendants())
                    yield return d;
            }
        }
    }
}
