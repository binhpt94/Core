using System;
using System.Collections.Generic;
using System.Linq;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Who has to say yes to a change request, from what it changes (not from what the asking agent says about it):
    ///   additive        new types in B's Contracts (nobody uses them yet)                      → the gate, at once
    ///   breaking        removes/changes a line, or adds a member to an EXISTING type (breaks its
    ///                   implementers and every fake of it)                                     → an arbiter session (not the asker)
    ///   meaning         no API: B's behaviour / requirement changes                            → the owner (batched: `cr decide`)
    ///   core-additive   new types in Core/Runtime (shared vocabulary of every feature)         → an arbiter session
    ///   core-breaking / core-meaning                                                           → the owner
    /// </summary>
    public static class CrClassifier
    {
        public const string Additive = "additive", Breaking = "breaking", Meaning = "meaning";
        public const string Gate = "gate", ArbiterRoute = "arbiter", Owner = "owner";

        /// <param name="currentApi">the target's PublicApi.txt today ("" when it has none)</param>
        /// <param name="api">proposed lines: a type header (`interface Game.B.Contracts.INew`) then its members indented; "- line" removes</param>
        public static (string kind, string error) Classify(string currentApi, IList<string> api, bool core)
        {
            string prefix = core ? "core-" : "";
            if (api == null || api.Count == 0)
                return (prefix + Meaning, null);
            var types = Types(currentApi);
            bool breaking = false;
            string header = null;
            foreach (var raw in api)
            {
                string line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    breaking = true;
                    continue;
                }
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    header = line.Trim();
                    continue;
                }
                if (header == null)
                    return (null, "member line '" + line.Trim() + "' needs its type header first (e.g. --api \"interface Game.B.Contracts.IX\" --api \"  System.Int32 Count { get; }\")");
                if (types.ContainsKey(header))
                    breaking = true; // a new member on an existing type: its implementers and fakes no longer compile
            }
            return (prefix + (breaking ? Breaking : Additive), null);
        }

        public static string Route(string kind) =>
            kind == Additive ? Gate
            : kind == Breaking || kind == "core-" + Additive ? ArbiterRoute
            : Owner;

        /// <summary>PublicApi.txt → type header → its member lines (format of ARCH023's Describe).</summary>
        public static Dictionary<string, List<string>> Types(string api)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> current = null;
            foreach (var raw in (api ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim().Length == 0)
                    continue;
                if (!char.IsWhiteSpace(raw[0]))
                    map[raw.Trim()] = current = new List<string>();
                else
                    current?.Add(raw.Trim());
            }
            return map;
        }

        /// <summary>
        /// A PublicApi.txt edit made under a CR grant: every added line must be one of the CR's lines, every removed line one of its
        /// "- " lines. Anything else is the asker changing B's contract beyond what was approved.
        /// </summary>
        public static List<string> ApiDiffViolations(string before, string after, IEnumerable<string> crApi)
        {
            var adds = new HashSet<string>(StringComparer.Ordinal);
            var removes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in crApi)
            {
                string t = l.Trim();
                if (t.StartsWith("- ", StringComparison.Ordinal))
                    removes.Add(t.Substring(2).Trim());
                else if (t.Length > 0)
                    adds.Add(t);
            }
            var oldLines = Lines(before);
            var newLines = Lines(after);
            var problems = new List<string>();
            foreach (var l in Minus(newLines, oldLines).Where(l => !adds.Contains(l)))
                problems.Add("added a line the CR does not contain: " + l);
            foreach (var l in Minus(oldLines, newLines).Where(l => !removes.Contains(l)))
                problems.Add("removed a line the CR does not remove: " + l);
            return problems;
        }

        private static List<string> Lines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        private static IEnumerable<string> Minus(List<string> a, List<string> b)
        {
            var pool = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var x in a)
            {
                if (pool.TryGetValue(x, out int n) && n > 0)
                    pool[x] = n - 1;
                else
                    yield return x;
            }
        }
    }
}
