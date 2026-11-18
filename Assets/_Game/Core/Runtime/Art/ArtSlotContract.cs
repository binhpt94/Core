using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Core
{
    public enum ArtSlotKind
    {
        /// <summary>uGUI RectTransform; sizes in canvas units.</summary>
        UiImage,
        /// <summary>SpriteRenderer; sizes in 1/100 world units.</summary>
        Sprite2D,
        /// <summary>MeshRenderer / SkinnedMeshRenderer; sizes in 1/100 world units.</summary>
        Model3D
    }

    /// <summary>
    /// A gameplay action the art may animate. Gameplay owns the timing (in ticks); the art only has to put its marker
    /// (an animation event / frame tag named <see cref="Marker"/>) inside [MinTick, MaxTick] at some playback speed in the
    /// allowed range. Animation never drives gameplay: it is checked against it.
    /// </summary>
    public sealed class ArtAction
    {
        public string Clip { get; }
        public string Marker { get; }
        public int MinTick { get; }
        public int MaxTick { get; }
        public int MinSpeedPercent { get; }
        public int MaxSpeedPercent { get; }
        /// <summary>false: a static asset without this clip is fine; if the clip exists, its timing is still checked.</summary>
        public bool Required { get; }

        public ArtAction(string clip, string marker, int minTick, int maxTick, int minSpeedPercent = 100, int maxSpeedPercent = 100, bool required = true)
        {
            Clip = clip;
            Marker = marker;
            MinTick = minTick;
            MaxTick = maxTick;
            MinSpeedPercent = minSpeedPercent;
            MaxSpeedPercent = maxSpeedPercent;
            Required = required;
        }
    }

    /// <summary>
    /// The measurable promise between gameplay and art for one slot (stable id "Feature.Name"). Gameplay footprint stays in
    /// the simulation; this is the VISUAL envelope art may use, the pivot it must keep and the actions it must time.
    /// Integers only: percentages, canvas units or 1/100 world units, ticks.
    /// </summary>
    public sealed class ArtSlotContract
    {
        public string Id { get; }
        public ArtSlotKind Kind { get; }
        public int MinWidth { get; }
        public int MaxWidth { get; }
        public int MinHeight { get; }
        public int MaxHeight { get; }
        public int PivotXPercent { get; }
        public int PivotYPercent { get; }
        public int PivotTolerancePercent { get; }
        public int TickRate { get; }
        public IReadOnlyList<ArtAction> Actions { get; }

        public ArtSlotContract(string id, ArtSlotKind kind, int minWidth, int maxWidth, int minHeight, int maxHeight,
            int pivotXPercent, int pivotYPercent, int pivotTolerancePercent, int tickRate, IReadOnlyList<ArtAction> actions)
        {
            Id = id;
            Kind = kind;
            MinWidth = minWidth;
            MaxWidth = maxWidth;
            MinHeight = minHeight;
            MaxHeight = maxHeight;
            PivotXPercent = pivotXPercent;
            PivotYPercent = pivotYPercent;
            PivotTolerancePercent = pivotTolerancePercent;
            TickRate = tickRate;
            Actions = actions ?? new ArtAction[0];
        }

        /// <summary>Self-consistency (a contract nobody can meet is a design bug, not an art bug).</summary>
        public IReadOnlyList<string> Problems()
        {
            var p = new List<string>();
            if (string.IsNullOrEmpty(Id) || Id.IndexOf('.') <= 0)
                p.Add("id must be 'Feature.Name'");
            if (MinWidth < 1 || MaxWidth < MinWidth || MinHeight < 1 || MaxHeight < MinHeight)
                p.Add("envelope must have 1 <= min <= max");
            if (PivotXPercent < 0 || PivotXPercent > 100 || PivotYPercent < 0 || PivotYPercent > 100 || PivotTolerancePercent < 0)
                p.Add("pivot must be 0..100 % with tolerance >= 0");
            if (TickRate < 1)
                p.Add("tick rate must be >= 1");
            foreach (var a in Actions)
                if (string.IsNullOrEmpty(a.Clip) || string.IsNullOrEmpty(a.Marker) || a.MinTick < 0 || a.MaxTick < a.MinTick || a.MinSpeedPercent < 1 || a.MaxSpeedPercent < a.MinSpeedPercent)
                    p.Add("action " + a.Clip + ": needs clip, marker, 0 <= minTick <= maxTick, 1 <= minSpeed <= maxSpeed");
            if (Actions.Select(a => a.Clip).Distinct(StringComparer.Ordinal).Count() != Actions.Count)
                p.Add("duplicate action clips");
            return p;
        }
    }

    /// <summary>Each feature lists its slots (implemented in its Logic; found by the Editor art gate and the rules).</summary>
    public interface IArtSlotCatalog
    {
        IReadOnlyList<ArtSlotContract> Slots { get; }
    }

    /// <summary>What the art gate measured on one asset (filled by the Unity probe; judged by pure code).</summary>
    public sealed class ArtMeasurement
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int PivotXPercent { get; set; }
        public int PivotYPercent { get; set; }
        /// <summary>Clip name → (length ms, marker name → time ms).</summary>
        public Dictionary<string, ArtClip> Clips { get; } = new Dictionary<string, ArtClip>(StringComparer.Ordinal);
    }

    public sealed class ArtClip
    {
        public int LengthMs { get; set; }
        public Dictionary<string, int> MarkersMs { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Contract vs measurement: one readable line per problem, e.g. "A-SIZE width 96 > max 72".</summary>
    public static class ArtChecks
    {
        public static List<string> Check(ArtSlotContract c, ArtMeasurement m)
        {
            var v = new List<string>();
            if (m.Width < c.MinWidth) v.Add("A-SIZE width " + m.Width + " < min " + c.MinWidth);
            if (m.Width > c.MaxWidth) v.Add("A-SIZE width " + m.Width + " > max " + c.MaxWidth);
            if (m.Height < c.MinHeight) v.Add("A-SIZE height " + m.Height + " < min " + c.MinHeight);
            if (m.Height > c.MaxHeight) v.Add("A-SIZE height " + m.Height + " > max " + c.MaxHeight);
            if (Math.Abs(m.PivotXPercent - c.PivotXPercent) > c.PivotTolerancePercent || Math.Abs(m.PivotYPercent - c.PivotYPercent) > c.PivotTolerancePercent)
                v.Add("A-PIVOT pivot " + m.PivotXPercent + "%," + m.PivotYPercent + "% != " + c.PivotXPercent + "%," + c.PivotYPercent + "% ±" + c.PivotTolerancePercent);
            foreach (var a in c.Actions)
            {
                if (!m.Clips.TryGetValue(a.Clip, out var clip))
                {
                    if (a.Required)
                        v.Add("A-CLIP missing clip '" + a.Clip + "'");
                    continue;
                }
                if (!clip.MarkersMs.TryGetValue(a.Marker, out int at))
                {
                    v.Add("A-MARK clip '" + a.Clip + "' has no marker '" + a.Marker + "'");
                    continue;
                }
                // Marker time at playback speed s: at / s. It must fall in [minTick, maxTick] for some s in the allowed range.
                long fastest = (long)at * 100 / a.MaxSpeedPercent, slowest = (long)at * 100 / a.MinSpeedPercent;
                long windowMin = (long)a.MinTick * 1000 / c.TickRate, windowMax = (long)a.MaxTick * 1000 / c.TickRate;
                if (slowest < windowMin || fastest > windowMax)
                    v.Add("A-TIME '" + a.Clip + "." + a.Marker + "' at " + at + " ms can play at " + fastest + "-" + slowest + " ms, gameplay window is " + windowMin + "-" + windowMax + " ms (ticks " + a.MinTick + "-" + a.MaxTick + ")");
            }
            return v;
        }

        /// <summary>The graybox's measurement: centre of the envelope, exact pivot, every action marker mid-window at 1x.</summary>
        public static ArtMeasurement Graybox(ArtSlotContract c)
        {
            var m = new ArtMeasurement
            {
                Width = (c.MinWidth + c.MaxWidth) / 2, Height = (c.MinHeight + c.MaxHeight) / 2,
                PivotXPercent = c.PivotXPercent, PivotYPercent = c.PivotYPercent
            };
            foreach (var a in c.Actions)
            {
                int mid = (a.MinTick + a.MaxTick) * 1000 / (2 * c.TickRate);
                var clip = new ArtClip { LengthMs = a.MaxTick * 1000 / c.TickRate + 100 };
                clip.MarkersMs[a.Marker] = mid;
                m.Clips[a.Clip] = clip;
            }
            return m;
        }

        /// <summary>Text snapshot line for one bound asset (diff-friendly, like the layout snapshot).</summary>
        public static string Describe(string slot, string level, ArtMeasurement m) =>
            slot + " [" + level + "] " + m.Width + "x" + m.Height + " pivot " + m.PivotXPercent + "%," + m.PivotYPercent + "%" +
            string.Concat(m.Clips.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => " " + k.Key + ":" + k.Value.LengthMs + "ms" +
                string.Concat(k.Value.MarkersMs.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => "/" + x.Key + "@" + x.Value))));
    }
}
