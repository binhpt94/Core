using System.Collections.Generic;
using System.Linq;

namespace Game.Core.DevTools
{
    /// <summary>Limits a layout must respect. Defaults suit a 1080-wide reference canvas.</summary>
    public sealed class LayoutLimits
    {
        /// <summary>Minimum touch target in canvas units (~48 dp on a 1080 px wide phone ≈ 130; 96 keeps dense UIs possible).</summary>
        public int MinTouchTarget { get; set; } = 96;

        /// <summary>Allowed error in canvas units (rounding). Keep it 0–1: tolerance hides real bugs.</summary>
        public int Tolerance { get; set; } = 1;
    }

    public sealed class LayoutViolation
    {
        /// <summary>SAFE (content outside safe area), OVERLAP, TEXT (text clipped), TOUCH (target too small).</summary>
        public string Code { get; }
        public string Path { get; }
        public string Message { get; }

        public LayoutViolation(string code, string path, string message)
        {
            Code = code;
            Path = path;
            Message = message;
        }

        public override string ToString() => "L-" + Code + " " + Path + " " + Message;
    }

    /// <summary>
    /// Constraint checks instead of pixel-perfect assertions: they fail only when a change is visibly wrong
    /// (clipped text, overlapping or unreachable buttons), so moving something 2px passes as long as it still fits.
    /// Overlap is checked only within one top-level layer: an overlay (popup, summary) may cover the screen below it.
    /// </summary>
    public static class LayoutChecks
    {
        public static List<LayoutViolation> Check(LayoutNode root, LayoutRect safeArea, LayoutLimits limits = null)
        {
            limits = limits ?? new LayoutLimits();
            int tol = limits.Tolerance;
            var result = new List<LayoutViolation>();

            foreach (var layer in root.Children)
            {
                var content = new[] { layer }.Concat(layer.Descendants()).Where(IsContent).ToList();

                foreach (var n in content)
                {
                    if (!safeArea.Contains(n.Rect, tol))
                        result.Add(new LayoutViolation("SAFE", n.Path, "outside safe area by " + Outside(safeArea, n.Rect) + " (rect " + n.Rect + ", safe " + safeArea + ")"));

                    if (n.Kind == LayoutNodeKind.Text && n.NeededHeight > n.Rect.H + tol)
                        result.Add(new LayoutViolation("TEXT", n.Path, "text clipped: needs " + n.NeededHeight + " high, has " + n.Rect.H));

                    if (n.Kind == LayoutNodeKind.Interactive && (n.Rect.W < limits.MinTouchTarget || n.Rect.H < limits.MinTouchTarget))
                        result.Add(new LayoutViolation("TOUCH", n.Path, "touch target " + n.Rect.W + "x" + n.Rect.H + " < " + limits.MinTouchTarget));
                }

                for (int i = 0; i < content.Count; i++)
                    for (int j = i + 1; j < content.Count; j++)
                    {
                        var a = content[i];
                        var b = content[j];
                        if (a.IsAncestorOf(b) || b.IsAncestorOf(a))
                            continue;
                        var overlap = a.Rect.Intersect(b.Rect);
                        if (overlap.W > tol && overlap.H > tol)
                            result.Add(new LayoutViolation("OVERLAP", a.Path, "overlaps " + b.Path + " by " + overlap.W + "x" + overlap.H));
                    }
            }
            return result;
        }

        private static bool IsContent(LayoutNode n) =>
            !n.Rect.IsEmpty && (n.Kind == LayoutNodeKind.Interactive || (n.Kind == LayoutNodeKind.Text && !n.IsEmptyText));

        private static string Outside(LayoutRect safe, LayoutRect r)
        {
            var parts = new List<string>();
            if (r.Y < safe.Y) parts.Add((safe.Y - r.Y) + " top");
            if (r.Bottom > safe.Bottom) parts.Add((r.Bottom - safe.Bottom) + " bottom");
            if (r.X < safe.X) parts.Add((safe.X - r.X) + " left");
            if (r.Right > safe.Right) parts.Add((r.Right - safe.Right) + " right");
            return string.Join(", ", parts);
        }
    }
}
