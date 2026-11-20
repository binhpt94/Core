using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>One immutable art version of a slot: vNNN.png (+ vNNN.svg when Claude drew it).</summary>
    public sealed class ArtVersion
    {
        public string Id { get; set; }        // v001
        public string Date { get; set; }      // yyyy-MM-dd
        public string Tier { get; set; }      // candidate | final
        public string By { get; set; }        // claude | codex | human
        public string Parent { get; set; }    // vNNN it was made from, or "-"
        public string Note { get; set; } = "";
        public int Number => int.TryParse(Id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>
    /// Art/&lt;Name&gt;/versions.txt: the version history of one slot (text, diffable, written only by `art add` / `art use`).
    ///   v001 2026-11-21 candidate claude - "first draft"
    ///   v002 2026-11-21 final codex v001 "from v001"
    ///   use candidate v001
    ///   use final v002
    /// Versions are never rewritten; "use" picks what the game shows per tier (Unity's ArtImport builds the binding from it).
    /// </summary>
    public sealed class ArtVersionLog
    {
        public static readonly string[] Tiers = { "candidate", "final" };

        public List<ArtVersion> Versions { get; } = new List<ArtVersion>();
        public Dictionary<string, string> Use { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public string NextId() => "v" + (Versions.Count == 0 ? 1 : Versions.Max(v => v.Number) + 1).ToString("000", CultureInfo.InvariantCulture);
        public ArtVersion Find(string id) => Versions.FirstOrDefault(v => v.Id == id);

        public static ArtVersionLog Parse(string text)
        {
            var log = new ArtVersionLog();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var f = line.Split(new[] { ' ' }, 6, StringSplitOptions.RemoveEmptyEntries);
                if (f[0] == "use" && f.Length >= 3)
                    log.Use[f[1]] = f[2];
                else if (f[0].Length == 4 && f[0][0] == 'v' && f.Length >= 5)
                    log.Versions.Add(new ArtVersion
                    {
                        Id = f[0], Date = f[1], Tier = f[2], By = f[3], Parent = f[4],
                        Note = f.Length > 5 ? f[5].Trim().Trim('"') : ""
                    });
            }
            return log;
        }

        public string Format(string slotId)
        {
            var sb = new StringBuilder("# Art versions of " + slotId + ": written by `art add` / `art use`, never by hand. Files: vNNN.png (+ vNNN.svg).\n");
            foreach (var v in Versions.OrderBy(v => v.Number))
                sb.Append(v.Id).Append(' ').Append(v.Date).Append(' ').Append(v.Tier).Append(' ').Append(v.By).Append(' ').Append(v.Parent)
                  .Append(" \"").Append(v.Note.Replace('"', '\'').Replace('\n', ' ')).Append("\"\n");
            foreach (var tier in Tiers.Where(Use.ContainsKey))
                sb.Append("use ").Append(tier).Append(' ').Append(Use[tier]).Append('\n');
            return sb.ToString();
        }
    }

    public static class ArtVersions
    {
        public const string LogFile = "versions.txt";

        /// <summary>Root-relative folder of a slot's versions: Features/F/Assets/Private/Art/&lt;Name&gt;/.</summary>
        public static string Dir(string slotId)
        {
            int dot = slotId.IndexOf('.');
            return Layout.Unity + "Assets/_Game/Features/" + slotId.Substring(0, dot) + "/Assets/Private/Art/" + slotId.Substring(dot + 1) + "/";
        }

        public static ArtVersionLog Load(string root, string slotId)
        {
            string path = Layout.Full(root, Dir(slotId) + LogFile);
            return ArtVersionLog.Parse(File.Exists(path) ? File.ReadAllText(path) : "");
        }

        public static void Save(string root, string slotId, ArtVersionLog log)
        {
            string path = Layout.Full(root, Dir(slotId) + LogFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, log.Format(slotId));
        }

        /// <summary>The version a tier currently uses, or null.</summary>
        public static string InUse(string root, string slotId, string tier) =>
            tier != null && Load(root, slotId).Use.TryGetValue(tier, out var id) ? id : null;

        /// <summary>
        /// Adds a NEW version from a source file (never overwrites): .png is copied; .svg is kept as vNNN.svg and rendered to
        /// vNNN.png by <paramref name="renderSvg"/> (svg path, png path) → error or null. The tier then uses it unless keepUse.
        /// </summary>
        public static ArtVersion Add(string root, string slotId, string sourceFile, string tier, string by, string note, string parent,
            string date, Func<string, string, string> renderSvg, bool keepUse = false)
        {
            if (Array.IndexOf(ArtVersionLog.Tiers, tier) < 0)
                throw new ArgumentException("tier must be candidate or final");
            string ext = Path.GetExtension(sourceFile).ToLowerInvariant();
            if (ext != ".png" && ext != ".svg")
                throw new ArgumentException("source must be .png or .svg");
            var log = Load(root, slotId);
            if (parent != null && parent != "-" && log.Find(parent) == null)
                throw new ArgumentException("unknown parent version " + parent);
            var v = new ArtVersion { Id = log.NextId(), Date = date, Tier = tier, By = by, Parent = parent ?? "-", Note = note ?? "" };
            string dir = Layout.Full(root, Dir(slotId));
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, v.Id + ".png");
            if (ext == ".svg")
            {
                string svg = Path.Combine(dir, v.Id + ".svg");
                File.Copy(sourceFile, svg);
                string error = renderSvg(svg, png);
                if (error != null || !File.Exists(png))
                {
                    File.Delete(svg);
                    if (File.Exists(png))
                        File.Delete(png);
                    throw new InvalidOperationException("SVG render failed: " + (error ?? "no PNG written"));
                }
            }
            else
                File.Copy(sourceFile, png);
            log.Versions.Add(v);
            if (!keepUse)
                log.Use[tier] = v.Id;
            Save(root, slotId, log);
            return v;
        }

        /// <summary>Points a tier at an existing version (switch or roll back). Returns the previous one.</summary>
        public static string Use(string root, string slotId, string id, string tier)
        {
            var log = Load(root, slotId);
            var v = log.Find(id) ?? throw new ArgumentException("unknown version " + id + " (known: " + string.Join(", ", log.Versions.Select(x => x.Id)) + ")");
            tier = tier ?? v.Tier;
            if (Array.IndexOf(ArtVersionLog.Tiers, tier) < 0)
                throw new ArgumentException("tier must be candidate or final");
            log.Use.TryGetValue(tier, out var previous);
            log.Use[tier] = v.Id;
            Save(root, slotId, log);
            return previous;
        }
    }
}
