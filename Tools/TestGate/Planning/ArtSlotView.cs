using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>
    /// One row of the dashboard's Art tab: where the slot's art lives (module, files), what it looks like (thumbnail + contract box
    /// for the zoom), and whether the owner can tick it for acceptance (only a final that passed the automatic art gate).
    /// Paths are Unity-relative (Assets/...); Thumb/Compare are relative to Dashboard/ (the page's folder).
    /// </summary>
    public sealed class ArtSlotView
    {
        public string Id { get; set; }
        public string Module { get; set; }
        public List<string> Features { get; set; } = new List<string>();
        public string Level { get; set; }
        public string Gate { get; set; }
        public string Tier { get; set; }          // candidate | final | null (graybox)
        public string Version { get; set; }
        public string By { get; set; }
        public string Date { get; set; }
        public string Hash { get; set; }
        public string File { get; set; }          // bound prefab
        public string Png { get; set; }           // the version's PNG in the feature's Art folder
        public string Contract { get; set; }      // <F>ArtSlots.cs declaring the slot
        public string Thumb { get; set; }
        public int ThumbWidth { get; set; }
        public int ThumbHeight { get; set; }
        public string Compare { get; set; }
        public bool Acceptable { get; set; }
        public bool Accepted { get; set; }
        public string Refusal { get; set; }       // why it can't be ticked (plain Vietnamese), "" when it can
        public bool Debt { get; set; }
        public List<string> Violations { get; set; } = new List<string>();
        public ArtBox Box { get; set; }

        public sealed class ArtBox
        {
            public int MinWidth { get; set; }
            public int MaxWidth { get; set; }
            public int MinHeight { get; set; }
            public int MaxHeight { get; set; }
            public int PivotX { get; set; }
            public int PivotY { get; set; }
        }

        private static string UnityRel(string rootRel) =>
            rootRel != null && rootRel.StartsWith(Layout.Unity, StringComparison.Ordinal) ? rootRel.Substring(Layout.Unity.Length) : rootRel;

        public static List<ArtSlotView> Build(string root, IEnumerable<SlotState> slots, IReadOnlyList<FeatureState> features)
        {
            var contracts = ArtRules.Declarations(root).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().File, StringComparer.Ordinal);
            string specsFile = Layout.Full(root, ArtSlotSpecs.Path); // read as is: the dashboard never re-runs the export
            var specs = System.IO.File.Exists(specsFile) ? ArtSlotSpecs.Parse(System.IO.File.ReadAllText(specsFile)).ToDictionary(s => s.Id, StringComparer.Ordinal)
                : new Dictionary<string, ArtSlotSpec>(StringComparer.Ordinal);
            var list = new List<ArtSlotView>();
            foreach (var s in slots.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var v = new ArtSlotView
                {
                    Id = s.Id, Module = s.Id.Substring(0, Math.Max(0, s.Id.IndexOf('.'))), Level = s.Level, Gate = s.Gate, Tier = s.BoundAs,
                    Version = s.Version, Hash = s.Hash, File = UnityRel(s.BoundFile), Violations = s.Violations.ToList(),
                    Contract = contracts.TryGetValue(s.Id, out var c) ? UnityRel(c) : null
                };
                foreach (var f in features.Where(f => f.Slots.Contains(s.Id)))
                {
                    v.Features.Add(f.Id);
                    v.Debt |= f.ArtDebt.Any(d => d.StartsWith(s.Id + ":", StringComparison.Ordinal));
                }
                if (specs.TryGetValue(s.Id, out var spec))
                    v.Box = new ArtBox { MinWidth = spec.MinWidth, MaxWidth = spec.MaxWidth, MinHeight = spec.MinHeight, MaxHeight = spec.MaxHeight, PivotX = spec.PivotXPercent, PivotY = spec.PivotYPercent };
                FillVersion(root, v);
                string compare = ArtBriefCli.BriefDir + s.Id + ".compare.html";
                if (System.IO.File.Exists(Layout.Full(root, compare)))
                    v.Compare = DashboardRel(compare);
                v.Accepted = s.Level == ArtLevels.Final;
                v.Refusal = v.Accepted ? "Đã duyệt bản final này." : Why(s);
                v.Acceptable = !v.Accepted && ArtCli.Refusal(s) == null;
                list.Add(v);
            }
            return list;
        }

        private static string DashboardRel(string rootRel) => rootRel.StartsWith("Dashboard/", StringComparison.Ordinal) ? rootRel.Substring("Dashboard/".Length) : rootRel;

        /// <summary>The version in use: metadata from versions.txt, its PNG copied next to the dashboard (works from file:// and `serve`).</summary>
        private static void FillVersion(string root, ArtSlotView v)
        {
            if (v.Version == null)
                return;
            var meta = ArtVersions.Load(root, v.Id).Versions.FirstOrDefault(x => x.Id == v.Version);
            v.By = meta?.By;
            v.Date = meta?.Date;
            string png = ArtVersions.Dir(v.Id) + v.Version + ".png";
            string full = Layout.Full(root, png);
            if (!System.IO.File.Exists(full))
                return;
            v.Png = UnityRel(png);
            string copy = ArtBriefCli.VersionCopyDir + v.Id + "/" + v.Version + ".png";
            string copyFull = Layout.Full(root, copy);
            Directory.CreateDirectory(Path.GetDirectoryName(copyFull));
            if (!System.IO.File.Exists(copyFull) || System.IO.File.GetLastWriteTimeUtc(copyFull) < System.IO.File.GetLastWriteTimeUtc(full))
                System.IO.File.Copy(full, copyFull, true);
            v.Thumb = DashboardRel(copy);
            (v.ThumbWidth, v.ThumbHeight) = VersionCard.PngSize(System.IO.File.ReadAllBytes(full));
        }

        /// <summary>Why the owner can't tick it, in the owner's words ("" = they can).</summary>
        private static string Why(SlotState s)
        {
            if (s.BoundFile == null)
                return "Chưa có art: đang dùng hình tạm (graybox).";
            if (s.BoundAs != "final")
                return "Bản thử: chỉ cần qua kiểm tra tự động, không cần duyệt." + (s.Gate == "failed" ? " Kiểm tra đang KHÔNG đạt: " + string.Join("; ", s.Violations.Take(2)) : "");
            if (s.Gate == "failed")
                return "Bản final KHÔNG đạt kiểm tra tự động: " + string.Join("; ", s.Violations.Take(2)) + ". Sửa art trước khi duyệt.";
            if (s.Gate != "passed")
                return "Bản final chưa chạy kiểm tra tự động (cần Unity). Chạy xong mới duyệt được.";
            return "";
        }
    }
}
