using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    public static class ArtLevels
    {
        public const string Graybox = "GRAYBOX";      // nothing bound: the generated placeholder (always meets the contract)
        public const string Candidate = "CANDIDATE";  // real art bound (candidate, or a final not accepted yet): only the automatic art gate applies
        public const string Stale = "STALE";          // a final accepted once, but its content changed since (acceptance is per content hash)
        public const string Final = "FINAL";          // bound as final, a human accepted THIS content (`art accept`), art gate passed

        public static int Rank(string level)
        {
            switch (level)
            {
                case Final: return 2;
                case Candidate: case Stale: return 1;
                default: return 0;
            }
        }

        /// <summary>Rank a plan level ([art: graybox|candidate|final]) needs; the legacy "approved" reads as candidate.</summary>
        public static int RankOfPlan(string planLevel) => planLevel == null ? -1 : Array.IndexOf(PlanParser.ArtLevels, PlanParser.NormalizeArtLevel(planLevel));
    }

    public sealed class SlotState
    {
        public string Id { get; set; }
        public string Level { get; set; }
        public string Binding { get; set; }        // binding asset path
        public string BoundFile { get; set; }      // prefab (or other asset) currently bound
        public string BoundAs { get; set; }        // candidate | final
        public string Hash { get; set; }           // content hash of the bound art (+ files it references)
        public string Version { get; set; }        // art version in use for the bound tier (Art/<Name>/versions.txt), null when not versioned
        public string Gate { get; set; }           // passed | failed | not run
        public string Measured { get; set; }
        public List<string> Violations { get; set; } = new List<string>();
        public string Detail { get; set; }
    }

    /// <summary>
    /// Art level of every slot, COMPUTED from the binding assets (Force Text YAML), the files they bind, the human approvals
    /// (Docs/Plan/art-accepted.txt: slot, content hash, date) and the art gate's measurements (Dashboard/Data/art). No AI estimates art.
    /// </summary>
    public static class ArtStatus
    {
        public const string AcceptedPath = "Docs/Plan/art-accepted.txt";
        private static readonly Regex MetaGuid = new Regex(@"^guid:\s*([0-9a-f]{32})", RegexOptions.Multiline);
        private static readonly Regex AnyGuid = new Regex(@"guid:\s*([0-9a-f]{32})");

        public static Dictionary<string, SlotState> Compute(string root)
        {
            var result = new Dictionary<string, SlotState>(StringComparer.Ordinal);
            var guids = GuidMap(root);
            var bindings = ArtRules.Bindings(root).GroupBy(b => b.SlotId).ToDictionary(g => g.Key, g => g.First());
            var accepted = LoadAccepted(root);
            foreach (var d in ArtRules.Declarations(root))
            {
                var s = new SlotState { Id = d.Id, Level = ArtLevels.Graybox, Gate = "not run", Detail = "graybox (generated from the contract)" };
                result[d.Id] = s;
                if (!bindings.TryGetValue(d.Id, out var b))
                    continue;
                s.Binding = b.Path;
                string guid = b.FinalGuid.Length > 0 ? b.FinalGuid : b.CandidateGuid;
                s.BoundAs = b.FinalGuid.Length > 0 ? "final" : b.CandidateGuid.Length > 0 ? "candidate" : null;
                if (s.BoundAs == null || !guids.TryGetValue(guid, out var file))
                {
                    if (s.BoundAs != null)
                        s.Detail = "bound asset guid " + guid + " not found (missing .meta?)";
                    s.BoundAs = null;
                    continue;
                }
                s.BoundFile = file;
                s.Hash = ContentHash(root, file, guids);
                ReadGate(root, s);
                s.Version = ArtVersions.InUse(root, d.Id, s.BoundAs);
                accepted.TryGetValue(d.Id, out var hashes);
                // Only a FINAL needs a human (`art accept`); every earlier version only has to pass the automatic art gate.
                if (s.BoundAs != "final")
                {
                    s.Level = ArtLevels.Candidate;
                    s.Detail = "real art bound (candidate): needs only the automatic art gate" + (s.Gate == "passed" ? " (passed)" : s.Gate == "not run" ? " (pending)" : "");
                }
                else if (hashes != null && hashes.Contains(s.Hash)) // going back to an accepted final is accepted again
                {
                    s.Level = s.Gate == "passed" ? ArtLevels.Final : ArtLevels.Candidate;
                    s.Detail = s.Level == ArtLevels.Final ? "final, accepted by a human, art gate passed" : "final accepted by a human, waiting for the art gate";
                }
                else if (hashes != null && hashes.Count > 0)
                {
                    s.Level = ArtLevels.Stale;
                    s.Detail = "the final changed after acceptance: a human must look again (`art accept " + d.Id + "`)";
                }
                else
                {
                    s.Level = ArtLevels.Candidate;
                    s.Detail = s.Gate == "passed" ? "final bound, art gate passed: waiting for the owner (`art accept " + d.Id + "` or the dashboard's Art tab)"
                        : "final bound, waiting for the art gate and then the owner's acceptance";
                }
                if (s.Gate == "failed")
                    s.Detail += "; art gate FAILED: " + string.Join("; ", s.Violations.Take(3));
            }
            return result;
        }

        /// <summary>Hash of the bound file plus every file it references (sprites, clips, materials): any change counts.</summary>
        public static string ContentHash(string root, string file, Dictionary<string, string> guids)
        {
            string full = Path.Combine(root, file);
            if (!File.Exists(full))
                return "missing";
            string text = File.ReadAllText(full);
            var parts = new List<string> { PlanText.Hash(text) };
            foreach (var g in AnyGuid.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                if (guids.TryGetValue(g, out var dep) && File.Exists(Path.Combine(root, dep)))
                    parts.Add(PlanText.Hash(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, dep)))));
            return PlanText.Hash(string.Join("|", parts));
        }

        private static void ReadGate(string root, SlotState s)
        {
            string path = Path.Combine(root, "Dashboard", "Data", "art", s.Id + "." + s.BoundAs + ".txt");
            if (!File.Exists(path))
                return;
            if (File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(Path.Combine(root, s.BoundFile)))
            {
                s.Gate = "not run";
                s.Detail = "art changed after the last art gate run";
                return;
            }
            var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
            s.Measured = lines.FirstOrDefault();
            s.Violations = lines.Skip(1).ToList();
            s.Gate = s.Violations.Count == 0 ? "passed" : "failed";
        }

        /// <summary>Slot → every FINAL content hash a human accepted (one line per acceptance; switching versions keeps them).</summary>
        public static Dictionary<string, HashSet<string>> LoadAccepted(string root)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            string p = Path.Combine(root, AcceptedPath);
            if (File.Exists(p))
                foreach (var line in File.ReadAllLines(p))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // Acceptance is for finals only: an old line for a candidate binding is history and counts for nothing.
                    bool candidateLine = parts.Length >= 4 && parts[3].IndexOf(".candidate.", StringComparison.Ordinal) >= 0;
                    if (parts.Length >= 2 && !parts[0].StartsWith("#", StringComparison.Ordinal) && !candidateLine)
                    {
                        if (!map.TryGetValue(parts[0], out var set))
                            map[parts[0]] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(parts[1]);
                    }
                }
            return map;
        }

        public static void Accept(string root, SlotState s)
        {
            string p = Path.Combine(root, AcceptedPath);
            var lines = File.Exists(p) ? File.ReadAllLines(p).Where(l => !l.StartsWith(s.Id + " " + s.Hash + " ", StringComparison.Ordinal)).ToList()
                : new List<string> { "# Art accepted by a human: <slot> <content hash> <date> <file> [version]. Written by `art accept`; one line per approved content." };
            lines.Add(s.Id + " " + s.Hash + " " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + s.BoundFile + (s.Version != null ? " " + s.Version : ""));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllLines(p, lines);
        }

        public static Dictionary<string, string> GuidMap(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string assets = Path.Combine(root, "UnityProject", "Assets");
            if (Directory.Exists(assets))
                foreach (var meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
                {
                    var m = MetaGuid.Match(File.ReadAllText(meta));
                    if (m.Success)
                        map[m.Groups[1].Value] = PathInfo.Normalize(meta.Substring(root.Length, meta.Length - root.Length - 5));
                }
            return map;
        }
    }
}
