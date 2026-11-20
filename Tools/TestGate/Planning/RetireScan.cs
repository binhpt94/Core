using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>One place a dropped requirement still lives: a [Covers] on a test, or its id in code.</summary>
    public sealed class RetireLeftover
    {
        public string Requirement { get; set; }
        public string Path { get; set; }
        public int Line { get; set; }
        public string Kind { get; set; }   // covers | mention

        public override string ToString() => "RETIRE_LEFT " + Requirement + " " + Path + ":" + Line + " (" + Kind + ")";
    }

    /// <summary>
    /// What a cancelled ([dropped]) requirement left behind in game code. Code reached only by its tests is caught by ARCH033
    /// once those tests are gone (nothing references it any more), so the scan only needs the ids themselves.
    /// The rule engine and its tests are skipped: their fixtures use sample ids.
    /// </summary>
    public static class RetireScan
    {
        /// <summary>Gap actor: shown first by `roadmap next`, worked by `task start --retire`.</summary>
        public const string Actor = "retire";

        public static List<RetireLeftover> Leftovers(string root, IEnumerable<string> ids)
        {
            var result = new List<RetireLeftover>();
            var list = ids.Distinct().ToList();
            string game = System.IO.Path.Combine(root, "UnityProject", "Assets", "_Game");
            if (list.Count == 0 || !Directory.Exists(game))
                return result;
            foreach (var file in Directory.EnumerateFiles(game, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string rel = PathInfo.Normalize(file.Substring(root.Length));
                if (rel.Contains("/_Game/Architecture/") || rel.Contains("/_Game/Tests/Architecture/"))
                    continue;
                result.AddRange(In(rel, File.ReadAllText(file), list));
            }
            return result;
        }

        public static IEnumerable<RetireLeftover> In(string rel, string content, IList<string> ids)
        {
            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (var id in ids)
                    if (Regex.IsMatch(lines[i], @"(?<![A-Za-z0-9-])" + Regex.Escape(id) + @"(?![A-Za-z0-9-])"))
                        yield return new RetireLeftover { Requirement = id, Path = rel, Line = i + 1, Kind = Regex.IsMatch(lines[i], @"\bCovers\s*\(") ? "covers" : "mention" };
        }
    }
}
