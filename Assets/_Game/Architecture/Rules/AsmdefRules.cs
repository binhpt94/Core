using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    public sealed class AsmdefInfo
    {
        public string Path;          // repo-relative .asmdef path
        public string Folder;        // repo-relative folder
        public string Name;
        public string[] References;
        public string[] PrecompiledReferences = new string[0];
        public bool NoEngineReferences;
        public bool AutoReferencedFalse;
        public bool OverrideReferences;
        public string[] DefineConstraints = new string[0];
        public string[] IncludePlatforms = new string[0];
        public bool EditorOnly => IncludePlatforms.Length == 1 && IncludePlatforms[0] == "Editor";
        public PathInfo Location;
        public PathLayer Layer => Location.Layer;
        public string Feature => Location.Feature;
        public bool IsPure => Location.IsPure;
    }

    /// <summary>Whole-graph rules over every .asmdef under Assets/_Game (convention-based: no allowlist files).</summary>
    public static class AsmdefRules
    {
        public const string DevToolsConstraint = "UNITY_EDITOR || GAME_DEVTOOLS";

        public static List<AsmdefInfo> Load(string repoRoot)
        {
            string game = Path.Combine(repoRoot, "Assets", "_Game");
            return Directory.GetFiles(game, "*.asmdef", SearchOption.AllDirectories)
                .Select(f => Parse(repoRoot, f))
                .Where(a => a.Layer != PathLayer.Architecture)
                .ToList();
        }

        public static IEnumerable<RuleViolation> Check(string repoRoot) =>
            Check(repoRoot, ArchitecturePolicy.Load(repoRoot));

        public static IEnumerable<RuleViolation> Check(string repoRoot, ArchitecturePolicy policy)
        {
            var all = Load(repoRoot);
            var byName = new Dictionary<string, AsmdefInfo>();
            foreach (var a in all)
                byName[a.Name] = a;

            foreach (var a in all)
            {
                var gameRefs = a.References.Where(r => r.StartsWith("Game.", StringComparison.Ordinal)).ToList();

                if (a.IsPure && !a.NoEngineReferences)
                    yield return V("ARCH001", a, "set \"noEngineReferences\": true");

                if (a.IsPure)
                    foreach (var r in a.References.Where(r => !r.StartsWith("Game.", StringComparison.Ordinal)
                                                              && policy.VendorOf(r)?.Tier != VendorTier.Foundation))
                        yield return V("ARCH002", a, "references '" + r + "'; pure code may use only Game.* and foundation vendors (" + ArchitecturePolicy.FileName + ")");

                foreach (var r in a.References.Where(r => r.StartsWith("GUID:", StringComparison.Ordinal)))
                    yield return V("ARCH003", a, r + " (turn off 'Use GUIDs' in the asmdef inspector)");

                string expected = ExpectedName(a);
                if (expected != null && a.Name != expected)
                    yield return V("ARCH004", a, "should be named " + expected);

                if (a.Layer == PathLayer.Contracts)
                    foreach (var r in gameRefs.Where(r => r != "Game.Core"))
                        yield return V("ARCH005", a, "references " + r);

                if (a.IsPure)
                    foreach (var r in a.References)
                        if ((byName.TryGetValue(r, out var t) && !t.IsPure && t.Layer != PathLayer.Tests)
                            || r.StartsWith("Unity", StringComparison.Ordinal))
                            yield return V("ARCH006", a, "references Unity-side assembly " + r);

                foreach (var r in gameRefs)
                {
                    if (!byName.TryGetValue(r, out var t) || t.Feature == null)
                        continue;

                    bool crossFeature = t.Feature != a.Feature;
                    if (a.Feature != null && crossFeature && t.Layer != PathLayer.Contracts)
                        yield return V("ARCH007", a, "references " + r + "; use Game." + t.Feature + ".Contracts");

                    bool targetUnitySide = t.Layer == PathLayer.Unity || t.Layer == PathLayer.Editor;
                    if (targetUnitySide && crossFeature && a.Layer != PathLayer.Bootstrap && a.Layer != PathLayer.Tests)
                        yield return V("ARCH008", a, "references " + r);

                    if (a.Layer == PathLayer.Bootstrap && t.Layer == PathLayer.Logic)
                        yield return V("ARCH012", a, "references " + r + "; wire the feature through its public Installer in Game." + t.Feature + ".Unity");
                }

                bool isTest = a.Layer == PathLayer.Tests;
                if (!a.AutoReferencedFalse)
                    yield return V("ARCH011", a, "set \"autoReferenced\": false (explicit references only)");
                if (a.OverrideReferences && !isTest)
                {
                    // Adapters may reference their own vendor's DLLs explicitly; nothing else may.
                    bool adapterOwnDlls = a.PrecompiledReferences.Length > 0 && a.PrecompiledReferences.All(d =>
                    {
                        var v = policy.VendorOf(d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? d.Substring(0, d.Length - 4) : d);
                        return v != null && (v.Tier != VendorTier.Sdk || v.Adapter == a.Name);
                    });
                    if (!adapterOwnDlls)
                        yield return V("ARCH011", a, "overrideReferences/precompiledReferences are reserved for tests and for adapters referencing their registered vendor DLLs");
                }
            }

            // ARCH102: dev tools (fixtures, scenarios, cheats) never reach a release build.
            foreach (var a in all)
            {
                if (a.Location.IsDevTools && !a.DefineConstraints.Any(d => d.Replace(" ", "") == DevToolsConstraint.Replace(" ", "")))
                    yield return V("ARCH102", a, "dev-tools assembly must set \"defineConstraints\": [\"" + DevToolsConstraint + "\"]");
                bool mayUseDevTools = a.Location.IsDevTools || a.EditorOnly || a.Layer == PathLayer.Tests;
                if (!mayUseDevTools)
                    foreach (var r in a.References)
                        if (byName.TryGetValue(r, out var t) && t.Location.IsDevTools)
                            yield return V("ARCH102", a, "runtime assembly references dev-tools assembly " + r + "; only Editor, tests and other dev tools may");
            }

            foreach (var cycle in Cycles(all))
                yield return new RuleViolation("ARCH009", "", 0, cycle);

            // ARCH010: README + Tests per feature
            string features = Path.Combine(repoRoot, "Assets", "_Game", "Features");
            if (Directory.Exists(features))
            {
                foreach (var dir in Directory.GetDirectories(features))
                {
                    string feature = System.IO.Path.GetFileName(dir);
                    string rel = PathInfo.GameRoot + "Features/" + feature;
                    if (!File.Exists(System.IO.Path.Combine(dir, "README.md")))
                        yield return new RuleViolation("ARCH010", rel, 0, "missing README.md");
                    bool hasLogic = all.Any(a => a.Feature == feature && a.Layer == PathLayer.Logic);
                    bool hasTests = all.Any(a => a.Feature == feature && a.Layer == PathLayer.Tests);
                    if (hasLogic && !hasTests)
                        yield return new RuleViolation("ARCH010", rel, 0, "has Logic but no Tests assembly");
                }
            }
        }

        private static string ExpectedName(AsmdefInfo a)
        {
            if (a.Feature == null)
                return null;
            switch (a.Layer)
            {
                case PathLayer.Logic: return "Game." + a.Feature;
                case PathLayer.Contracts: return "Game." + a.Feature + ".Contracts";
                case PathLayer.Unity: return "Game." + a.Feature + ".Unity";
                case PathLayer.Editor: return "Game." + a.Feature + ".Editor";
                case PathLayer.DevTools: return "Game." + a.Feature + ".DevTools";
                case PathLayer.Tests:
                    return a.Folder.EndsWith("/Editor/Tests", StringComparison.Ordinal)
                        ? "Game." + a.Feature + ".Editor.Tests"
                        : "Game." + a.Feature + ".Tests";
                default: return "(asmdef not allowed in " + a.Layer + " folder)";
            }
        }

        private static RuleViolation V(string id, AsmdefInfo a, string message) =>
            new RuleViolation(id, a.Path, 0, a.Name + ": " + message);

        private static AsmdefInfo Parse(string repoRoot, string file)
        {
            string json = File.ReadAllText(file);
            string rel = PathInfo.Normalize(file.Substring(repoRoot.Length));
            return new AsmdefInfo
            {
                Path = rel,
                Folder = rel.Substring(0, rel.LastIndexOf('/')),
                Location = PathInfo.Of(rel),
                Name = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value,
                NoEngineReferences = Regex.IsMatch(json, "\"noEngineReferences\"\\s*:\\s*true"),
                AutoReferencedFalse = Regex.IsMatch(json, "\"autoReferenced\"\\s*:\\s*false"),
                OverrideReferences = Regex.IsMatch(json, "\"overrideReferences\"\\s*:\\s*true"),
                References = StringArray(json, "references"),
                PrecompiledReferences = StringArray(json, "precompiledReferences"),
                DefineConstraints = StringArray(json, "defineConstraints"),
                IncludePlatforms = StringArray(json, "includePlatforms"),
            };
        }

        private static string[] StringArray(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            return m.Success
                ? Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToArray()
                : new string[0];
        }

        private static IEnumerable<string> Cycles(List<AsmdefInfo> all)
        {
            var graph = all.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().References.Where(r => r.StartsWith("Game.", StringComparison.Ordinal)).ToList());
            var state = new Dictionary<string, int>();
            var found = new List<string>();
            foreach (var n in graph.Keys)
                Visit(n, graph, state, new Stack<string>(), found);
            return found;
        }

        private static void Visit(string n, Dictionary<string, List<string>> g, Dictionary<string, int> state, Stack<string> path, List<string> found)
        {
            state.TryGetValue(n, out int s);
            if (s == 2) return;
            if (s == 1)
            {
                found.Add("cycle: " + string.Join(" -> ", path.Reverse().SkipWhile(x => x != n)) + " -> " + n);
                return;
            }
            state[n] = 1;
            path.Push(n);
            if (g.TryGetValue(n, out var edges))
                foreach (var e in edges)
                    Visit(e, g, state, path, found);
            path.Pop();
            state[n] = 2;
        }
    }
}
