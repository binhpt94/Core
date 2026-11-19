using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>One `[Covers("R-ID")]` on a test method (or on a test class: then Method is null = every test in it).</summary>
    public sealed class CoverEntry
    {
        public string Requirement { get; set; }
        public string File { get; set; }
        public string Class { get; set; }
        public string Method { get; set; }
        public int Line { get; set; }

        public string TestKey => Class + "." + (Method ?? "*");
    }

    /// <summary>
    /// ARCH110: Docs/Plan/features.md parses and is consistent (unique ids, known deps, no cycles, modules exist).
    /// ARCH111: every [Covers("R-...")] in tests names a requirement that exists in the plan.
    /// Requirement → test links live IN THE TEST CODE, so they move with the tests and can't drift in a separate table.
    /// </summary>
    public static class PlanRules
    {
        private static readonly Regex CoversAttr = new Regex(@"\bCovers\s*\(\s*""(?<id>[^""]*)""\s*\)");
        private static readonly Regex NextDecl = new Regex(@"\bclass\s+(?<class>\w+)|\b(?:void|Task|UniTask|IEnumerator)\s+(?<method>\w+)\s*\(");
        private static readonly Regex ClassDecl = new Regex(@"\bclass\s+(?<name>\w+)");

        public static IEnumerable<RuleViolation> Check(string repoRoot)
        {
            var plan = PlanParser.Load(repoRoot);
            if (plan == null)
                yield break; // a project without a plan is allowed; the planning commands say so
            foreach (var e in plan.Errors)
                yield return new RuleViolation("ARCH110", PlanParser.RelativePath, LineOf(e), e);
            foreach (var v in Consistency(repoRoot, plan))
                yield return v;

            var ids = new HashSet<string>(plan.AllRequirements.Select(r => r.Id), StringComparer.Ordinal);
            foreach (var c in Covers(repoRoot))
                if (!ids.Contains(c.Requirement))
                    yield return new RuleViolation("ARCH111", c.File, c.Line, "[Covers(\"" + c.Requirement + "\")] names no requirement in " + PlanParser.RelativePath +
                                                                             " (typo, or the requirement was removed: ask the human / propose it in " + PlanParser.InboxFolder + ")");
        }

        /// <summary>ARCH110 on a proposed text (a draft, or what an edit would write): the same checks as on the file, nothing read from it.</summary>
        public static IEnumerable<RuleViolation> CheckText(string repoRoot, string text)
        {
            var plan = PlanParser.Parse(text);
            foreach (var e in plan.Errors)
                yield return new RuleViolation("ARCH110", PlanParser.RelativePath, LineOf(e), e);
            foreach (var v in Consistency(repoRoot, plan))
                yield return v;
        }

        private static IEnumerable<RuleViolation> Consistency(string repoRoot, Plan plan)
        {
            string P = PlanParser.RelativePath;
            foreach (var f in plan.Features)
            {
                foreach (var m in f.Modules)
                    if (!Directory.Exists(Path.Combine(repoRoot, "UnityProject", "Assets", "_Game", "Features", m)))
                        yield return new RuleViolation("ARCH110", P, f.Line, f.Id + ": module '" + m + "' is not a folder in Assets/_Game/Features");
                if (f.Active && f.Modules.Count == 0)
                    yield return new RuleViolation("ARCH110", P, f.Line, f.Id + ": add [modules: X] (the code folders this feature lives in: task scope and change impact use it)");
                foreach (var d in f.Dependencies)
                {
                    if (d.Feature == f.Id)
                        yield return new RuleViolation("ARCH110", P, d.Line, f.Id + " depends on itself");
                    else if (plan.Feature(d.Feature) == null)
                        yield return new RuleViolation("ARCH110", P, d.Line, f.Id + " depends on unknown feature " + d.Feature);
                }
            }
            var slotIds = new HashSet<string>(ArtRules.Declarations(repoRoot).Select(d => d.Id), StringComparer.Ordinal);
            foreach (var f in plan.Features)
            {
                foreach (var slot in f.Slots.Where(x => !slotIds.Contains(x)))
                    yield return new RuleViolation("ARCH110", P, f.Line, f.Id + ": slot '" + slot + "' is not declared in any *ArtSlots catalog");
                if (f.ArtLevel != null && f.Slots.Count == 0)
                    yield return new RuleViolation("ARCH110", P, f.Line, f.Id + ": [art: " + f.ArtLevel + "] needs [slots: ...] (which art it is about)");
                if (f.ArtRisk && !f.Requirements.Any(r => r.IsFeasibility))
                    yield return new RuleViolation("ARCH110", P, f.Line, f.Id + ": [art-risk] needs a requirement tagged [feasibility] (what art must prove first)");
            }
            foreach (var cycle in Cycles(plan))
                yield return new RuleViolation("ARCH110", P, 0, "dependency cycle: " + cycle);
        }

        private static IEnumerable<string> Cycles(Plan plan)
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var path = new Stack<string>();
            var found = new List<string>();
            void Visit(PlanFeature f)
            {
                state.TryGetValue(f.Id, out int s);
                if (s == 2) return;
                if (s == 1)
                {
                    found.Add(string.Join(" -> ", path.Reverse().SkipWhile(x => x != f.Id)) + " -> " + f.Id);
                    return;
                }
                state[f.Id] = 1;
                path.Push(f.Id);
                foreach (var d in f.Dependencies)
                {
                    var t = plan.Feature(d.Feature);
                    if (t != null)
                        Visit(t);
                }
                path.Pop();
                state[f.Id] = 2;
            }
            foreach (var f in plan.Features)
                Visit(f);
            return found.Distinct();
        }

        /// <summary>All [Covers] in test sources under Assets/_Game (comment-stripped).</summary>
        public static List<CoverEntry> Covers(string repoRoot)
        {
            var result = new List<CoverEntry>();
            string game = Path.Combine(repoRoot, "UnityProject", "Assets", "_Game");
            if (!Directory.Exists(game))
                return result;
            foreach (var file in Directory.EnumerateFiles(game, "*.cs", SearchOption.AllDirectories))
            {
                string rel = PathInfo.Normalize(file.Substring(repoRoot.Length));
                if (!PathInfo.Of(rel).IsTest)
                    continue;
                result.AddRange(CoversIn(rel, File.ReadAllText(file)));
            }
            return result;
        }

        public static IEnumerable<CoverEntry> CoversIn(string relPath, string content)
        {
            string code = SourceText.StripComments(content);
            foreach (Match m in CoversAttr.Matches(code))
            {
                var next = NextDecl.Match(code, m.Index + m.Length);
                string cls = null, method = null;
                if (next.Success && next.Groups["class"].Success)
                    cls = next.Groups["class"].Value;
                else
                {
                    method = next.Success ? next.Groups["method"].Value : null;
                    cls = EnclosingClass(code, m.Index);
                }
                yield return new CoverEntry { Requirement = m.Groups["id"].Value, File = relPath, Class = cls, Method = method, Line = SourceText.LineOf(code, m.Index) };
            }
        }

        /// <summary>Innermost class whose body is still open at index (nested helper classes before the test don't count).</summary>
        private static string EnclosingClass(string code, int index)
        {
            string best = null;
            foreach (Match c in ClassDecl.Matches(code.Substring(0, index)))
            {
                int open = code.IndexOf('{', c.Index);
                if (open < 0 || open > index)
                    continue;
                int depth = 0;
                for (int i = open; i < index; i++)
                {
                    if (code[i] == '{') depth++;
                    else if (code[i] == '}') depth--;
                }
                if (depth > 0)
                    best = c.Groups["name"].Value; // later matches are nested deeper
            }
            return best;
        }

        private static int LineOf(string error)
        {
            var m = Regex.Match(error, @"^line (\d+):");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
    }
}
