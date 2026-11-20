using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TestGate.Planning
{
    /// <summary>One gameplay action of a slot, as exported from the real contract (Game.Core.ArtAction).</summary>
    public sealed class ArtActionSpec
    {
        public string Clip { get; set; }
        public string Marker { get; set; }
        public int MinTick { get; set; }
        public int MaxTick { get; set; }
        public int MinSpeedPercent { get; set; }
        public int MaxSpeedPercent { get; set; }
        public bool Required { get; set; }
        public int Frames { get; set; }
    }

    /// <summary>
    /// A slot contract as plain data. TestGate does not compile Game.Core, so the contracts are exported by a pure test
    /// (LogicTests: ArtSlotExportTests → Dashboard/Data/art/slots.txt) from the REAL objects, never parsed from C# text.
    /// </summary>
    public sealed class ArtSlotSpec
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public int MinWidth { get; set; }
        public int MaxWidth { get; set; }
        public int MinHeight { get; set; }
        public int MaxHeight { get; set; }
        public int PivotXPercent { get; set; }
        public int PivotYPercent { get; set; }
        public int PivotTolerancePercent { get; set; }
        public int TickRate { get; set; }
        public string Intent { get; set; } = "";
        public List<ArtActionSpec> Actions { get; } = new List<ArtActionSpec>();

        public string Feature => Id.Substring(0, Math.Max(0, Id.IndexOf('.')));
        public string Name => Id.Substring(Id.IndexOf('.') + 1);
    }

    /// <summary>
    /// Diff-friendly line format: <c>slot\t&lt;id&gt;\t&lt;kind&gt;\t&lt;minW&gt;\t...\t&lt;intent&gt;</c> then one
    /// <c>action\t&lt;clip&gt;\t...</c> line per action. Tabs/newlines inside the intent are flattened to spaces.
    /// </summary>
    public static class ArtSlotSpecs
    {
        public const string Path = "Dashboard/Data/art/slots.txt";

        public static string Format(IEnumerable<ArtSlotSpec> slots)
        {
            var sb = new StringBuilder();
            foreach (var s in slots.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                sb.Append(string.Join("\t", "slot", s.Id, s.Kind, I(s.MinWidth), I(s.MaxWidth), I(s.MinHeight), I(s.MaxHeight),
                    I(s.PivotXPercent), I(s.PivotYPercent), I(s.PivotTolerancePercent), I(s.TickRate), Flat(s.Intent))).Append('\n');
                foreach (var a in s.Actions)
                    sb.Append(string.Join("\t", "action", a.Clip, a.Marker, I(a.MinTick), I(a.MaxTick), I(a.MinSpeedPercent), I(a.MaxSpeedPercent),
                        a.Required ? "required" : "optional", I(a.Frames))).Append('\n');
            }
            return sb.ToString();
        }

        public static List<ArtSlotSpec> Parse(string text)
        {
            var result = new List<ArtSlotSpec>();
            ArtSlotSpec current = null;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var f = raw.TrimEnd('\r').Split('\t');
                if (f[0] == "slot" && f.Length >= 12)
                {
                    current = new ArtSlotSpec
                    {
                        Id = f[1], Kind = f[2], MinWidth = N(f[3]), MaxWidth = N(f[4]), MinHeight = N(f[5]), MaxHeight = N(f[6]),
                        PivotXPercent = N(f[7]), PivotYPercent = N(f[8]), PivotTolerancePercent = N(f[9]), TickRate = N(f[10]), Intent = f[11]
                    };
                    result.Add(current);
                }
                else if (f[0] == "action" && f.Length >= 9 && current != null)
                {
                    current.Actions.Add(new ArtActionSpec
                    {
                        Clip = f[1], Marker = f[2], MinTick = N(f[3]), MaxTick = N(f[4]), MinSpeedPercent = N(f[5]), MaxSpeedPercent = N(f[6]),
                        Required = f[7] == "required", Frames = N(f[8])
                    });
                }
            }
            return result;
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static int N(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        private static string Flat(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
