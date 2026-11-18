using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// The architecture graph, derived from .asmdef files + source + the policy file. Deterministic
    /// (sorted, no timestamps, no AI) and cheap (milliseconds), so it is simply rebuilt every time.
    /// Nodes are "groups": Core, Bootstrap, each feature F, and F.Contracts.
    /// FAIL-CLOSED: anything the generator cannot read with certainty becomes a ParseError (ARCH090)
    /// instead of being silently left out of the diagram.
    /// </summary>
    public sealed class ArchitectureGraph
    {
        public sealed class Edge
        {
            public string From, To, Kind; // Kind: uses | wires | implements
            public override string ToString() => From + " " + Kind + " " + To;
        }

        public sealed class StateMachine
        {
            public string Owner, Enum, Feature, Table;
            public List<(string From, string Trigger, string To)> Transitions = new List<(string, string, string)>();
        }

        public sealed class FeatureInfo
        {
            public string Name;
            public bool HasContracts;
            public SortedSet<string> PublicTypes = new SortedSet<string>(StringComparer.Ordinal);
            public SortedSet<string> Vendors = new SortedSet<string>(StringComparer.Ordinal);
            public int PublicAssets, PrivateAssets, Tests;
        }

        public SortedDictionary<string, FeatureInfo> Features { get; } = new SortedDictionary<string, FeatureInfo>(StringComparer.Ordinal);
        public List<Edge> Edges { get; } = new List<Edge>();
        public List<StateMachine> StateMachines { get; } = new List<StateMachine>();
        public SortedDictionary<string, SortedSet<string>> VendorUsers { get; } = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        public List<RuleViolation> ParseErrors { get; } = new List<RuleViolation>();
        public ArchitecturePolicy Policy { get; private set; }
        public bool AssetsKnown { get; private set; }
        public string Root { get; private set; }

        private static readonly Regex PublicType = new Regex(@"^\s*public\s+(?:sealed\s+|static\s+|abstract\s+|readonly\s+)*(?:class|interface|struct|enum)\s+(\w+)", RegexOptions.Multiline);
        private static readonly Regex InterfaceDecl = new Regex(@"\binterface\s+(\w+)");
        private static readonly Regex TypeWithBases = new Regex(@"\b(?:class|struct|interface)\s+(\w+)(?:<[^>]*>)?\s*(?::\s*([^{]+))?\{");
        private static readonly Regex ClassDecl = new Regex(@"\b(?:class|struct)\s+(\w+)");
        private static readonly Regex TestAttr = new Regex(@"\[(Test|TestCase|UnityTest|TestCaseSource)\b");

        public static ArchitectureGraph Build(string root)
        {
            var g = new ArchitectureGraph { Policy = ArchitecturePolicy.Load(root), Root = root };
            var asmdefs = AsmdefRules.Load(root);
            var byName = asmdefs.GroupBy(a => a.Name).ToDictionary(x => x.Key, x => x.First());
            string game = Path.Combine(root, "UnityProject", "Assets", "_Game");

            foreach (var a in asmdefs.Where(a => a.Feature != null))
                g.Feature(a.Feature).HasContracts |= a.Layer == PathLayer.Contracts;

            // ---- uses / wires (compile-time) + vendor usage, incl. DLLs referenced via precompiledReferences
            var edges = new HashSet<string>();
            foreach (var a in asmdefs.Where(IsRuntime))
            {
                string from = Group(a);
                var refs = a.References.Concat(a.PrecompiledReferences.Select(StripDll).Where(d => d != "nunit.framework"));
                foreach (var r in refs)
                {
                    if (byName.TryGetValue(r, out var t) && IsRuntime(t))
                    {
                        string to = Group(t);
                        if (to != from && (a.Feature == null || t.Feature != a.Feature || t.Layer == PathLayer.Contracts))
                            edges.Add(from + "|" + to + "|" + (a.Layer == PathLayer.Bootstrap && t.Feature != null && t.Layer != PathLayer.Contracts ? "wires" : "uses"));
                        continue;
                    }
                    var v = g.Policy.VendorOf(r);
                    if (v == null)
                        continue;
                    if (!g.VendorUsers.TryGetValue(v.Id, out var users))
                        g.VendorUsers[v.Id] = users = new SortedSet<string>(StringComparer.Ordinal);
                    users.Add(from);
                    if (a.Feature != null)
                        g.Feature(a.Feature).Vendors.Add(v.Id);
                }
            }

            var files = Directory.Exists(game) ? Directory.GetFiles(game, "*.cs", SearchOption.AllDirectories) : new string[0];
            var code = files.ToDictionary(f => f, f => SourceText.StripComments(File.ReadAllText(f)));
            var info = files.ToDictionary(f => f, f => PathInfo.Of(Rel(root, f)));

            // ---- implements (runtime), following base types transitively (class X : Base, Base : IContract)
            var contractOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in files.Where(f => info[f].Layer == PathLayer.Contracts))
                foreach (Match m in InterfaceDecl.Matches(code[f]))
                    contractOwner[m.Groups[1].Value] = info[f].Feature + ".Contracts";

            var bases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var featureOfType = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in files.Where(f => info[f].IsGameCode && !info[f].IsTest && !info[f].IsEditorOnly && !info[f].IsDevTools))
                foreach (Match m in TypeWithBases.Matches(code[f]))
                {
                    var list = m.Groups[2].Success
                        ? m.Groups[2].Value.Split(',').Select(b => Regex.Replace(b.Trim(), @"<.*", "").Split('.').Last()).Where(b => b.Length > 0).ToList()
                        : new List<string>();
                    bases[m.Groups[1].Value] = list;
                    if (info[f].Feature != null && info[f].Layer != PathLayer.Contracts)
                        featureOfType[m.Groups[1].Value] = info[f].Feature;
                }
            foreach (var type in featureOfType)
                foreach (var iface in AllBases(type.Key, bases).Where(contractOwner.ContainsKey))
                    if (contractOwner[iface] != type.Value + ".Contracts")
                        edges.Add(type.Value + "|" + contractOwner[iface] + "|implements");

            // ---- public faces, tests, state tables
            foreach (var f in files)
            {
                var fi = info[f];
                if (fi.Feature == null)
                    continue;
                if (fi.IsTest)
                    g.Feature(fi.Feature).Tests += TestAttr.Matches(code[f]).Count;
                else if ((fi.Layer == PathLayer.Contracts || fi.Layer == PathLayer.Unity) && !fi.IsEditorOnly && !fi.IsDevTools)
                    foreach (Match m in PublicType.Matches(code[f]))
                        g.Feature(fi.Feature).PublicTypes.Add(m.Groups[1].Value);
            }
            foreach (var f in files.Where(f => info[f].IsGameCode && !info[f].IsTest && !info[f].IsDevTools && info[f].Layer != PathLayer.Core))
                StateTableParser.Parse(info[f], code[f], g.StateMachines, g.ParseErrors);

            // ---- assets per feature
            g.AssetsKnown = Directory.EnumerateFiles(Path.Combine(root, "UnityProject", "Assets"), "*.meta", SearchOption.AllDirectories).Any();
            foreach (var feature in g.Features.Values)
            {
                feature.PublicAssets = CountAssets(Path.Combine(game, "Features", feature.Name, "Assets", "Public"));
                feature.PrivateAssets = CountAssets(Path.Combine(game, "Features", feature.Name, "Assets", "Private"));
            }

            foreach (var e in edges.OrderBy(x => x, StringComparer.Ordinal))
            {
                var p = e.Split('|');
                g.Edges.Add(new Edge { From = p[0], To = p[1], Kind = p[2] });
            }
            g.StateMachines.Sort((x, y) => string.CompareOrdinal(x.Owner + "." + x.Table, y.Owner + "." + y.Table));
            return g;
        }

        private static IEnumerable<string> AllBases(string type, Dictionary<string, List<string>> bases)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(new[] { type });
            while (stack.Count > 0)
                if (bases.TryGetValue(stack.Pop(), out var list))
                    foreach (var b in list.Where(seen.Add))
                    {
                        yield return b;
                        stack.Push(b);
                    }
        }

        private FeatureInfo Feature(string name)
        {
            if (!Features.TryGetValue(name, out var f))
                Features[name] = f = new FeatureInfo { Name = name };
            return f;
        }

        internal static string StripDll(string d) =>
            d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? d.Substring(0, d.Length - 4) : d;

        private static bool IsRuntime(AsmdefInfo a) =>
            a.Layer != PathLayer.Tests && !a.Location.IsEditorOnly && !a.Location.IsDevTools && a.Layer != PathLayer.Architecture;

        private static string Group(AsmdefInfo a)
        {
            if (a.Feature != null)
                return a.Layer == PathLayer.Contracts ? a.Feature + ".Contracts" : a.Feature;
            bool core = a.Layer == PathLayer.Core || a.Folder.StartsWith(PathInfo.GameRoot + "Core/", StringComparison.Ordinal); // Core/Runtime + Core/Unity
            return core ? "Core" : a.Layer == PathLayer.Bootstrap ? "Bootstrap" : a.Name;
        }

        private static int CountAssets(string dir) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".meta", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)) : 0;

        private static string Rel(string root, string full) => PathInfo.Normalize(full.Substring(root.Length));

        // ------------------------------------------------------------------ queries (compact text for agents)

        public IEnumerable<string> UsesOf(string group) => Edges.Where(e => e.From == group && e.Kind != "implements").Select(e => e.To);
        public IEnumerable<string> UsedBy(string group) => Edges.Where(e => e.To == group && e.Kind != "implements").Select(e => e.From);

        /// <summary>Direct dependencies of a feature (incl. its Contracts), without itself.</summary>
        public IEnumerable<string> FeatureUses(string f) =>
            UsesOf(f).Concat(UsesOf(f + ".Contracts")).Where(x => x != f + ".Contracts").Distinct().OrderBy(x => x, StringComparer.Ordinal);

        /// <summary>Direct dependents of a feature (incl. of its Contracts), without itself.</summary>
        public IEnumerable<string> FeatureUsedBy(string f) =>
            UsedBy(f).Concat(UsedBy(f + ".Contracts")).Where(x => x != f).Distinct().OrderBy(x => x, StringComparer.Ordinal);

        /// <summary>Everything that may break if <paramref name="feature"/> (or its Contracts) changes.</summary>
        public SortedSet<string> Impact(string feature)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(new[] { feature, feature + ".Contracts" });
            while (queue.Count > 0)
                foreach (var u in UsedBy(queue.Dequeue()))
                    if (result.Add(u))
                        queue.Enqueue(u);
            result.Remove(feature);
            return result;
        }

        public string DescribeFeature(string name)
        {
            if (!Features.TryGetValue(name, out var f))
                return "unknown feature '" + name + "'. Features: " + string.Join(", ", Features.Keys);

            var sb = new StringBuilder();
            sb.Append(name).Append('\n');
            sb.Append("  uses: ").Append(List(FeatureUses(name))).Append(" | vendors: ").Append(List(f.Vendors)).Append('\n');
            sb.Append("  used-by: ").Append(List(FeatureUsedBy(name))).Append('\n');
            sb.Append("  implements: ").Append(List(Edges.Where(e => e.From == name && e.Kind == "implements").Select(e => e.To)))
              .Append(" | implemented-by: ").Append(List(Edges.Where(e => e.To == name + ".Contracts" && e.Kind == "implements").Select(e => e.From))).Append('\n');
            sb.Append("  public: ").Append(List(f.PublicTypes)).Append('\n');
            sb.Append("  assets: Public ").Append(f.PublicAssets).Append(", Private ").Append(f.PrivateAssets)
              .Append(AssetsKnown ? "" : " (asset refs UNKNOWN: no .meta files yet)").Append(" | tests: ").Append(f.Tests).Append('\n');
            foreach (var sm in StateMachines.Where(s => s.Feature == name))
                sb.Append("  states: ").Append(sm.Owner).Append('.').Append(sm.Table).Append(" (").Append(sm.Enum).Append("): ")
                  .Append(sm.Transitions.SelectMany(t => new[] { t.From, t.To }).Distinct().Count()).Append(" states, ")
                  .Append(sm.Transitions.Count).Append(" transitions\n");
            sb.Append("  page: ").Append(ArchitectureDocs.FeaturePagePath(name));
            return sb.ToString();
        }

        internal static string List(IEnumerable<string> xs)
        {
            var l = xs.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            return l.Count == 0 ? "-" : string.Join(", ", l);
        }

        private static readonly Regex Ctor = new Regex(@"\b(?:public|internal|private|protected)\s+(\w+)\s*\(([^)]*)\)\s*(?::\s*(?:this|base)\s*\([^)]*\)\s*)?\{");

        /// <summary>L3: constructor-injection graph of one feature ("Type &lt;- dependencies"). Exact, because DI is by constructor.</summary>
        public static string DescribeDependencyInjection(string root, string feature)
        {
            string dir = Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", feature);
            if (!Directory.Exists(dir))
                return "unknown feature '" + feature + "'";
            var lines = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var fi = PathInfo.Of(Rel(root, f));
                if (fi.IsTest || fi.IsEditorOnly || fi.IsDevTools)
                    continue;
                string c = SourceText.StripComments(File.ReadAllText(f));
                var classes = new HashSet<string>(ClassDecl.Matches(c).Cast<Match>().Select(m => m.Groups[1].Value));
                foreach (Match m in Ctor.Matches(c))
                {
                    if (!classes.Contains(m.Groups[1].Value))
                        continue;
                    var deps = m.Groups[2].Value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)
                        .Select(p => Regex.Replace(p, @"\s*=.*$", "")).Select(p => p.Substring(0, Math.Max(0, p.LastIndexOf(' '))).Trim())
                        .Where(t => t.Length > 0).ToList();
                    if (deps.Count > 0 && deps.All(d => char.IsLower(d[0])))
                        continue; // value objects (only primitives): not part of the wiring
                    lines.Add(m.Groups[1].Value + " <- " + (deps.Count == 0 ? "(none)" : string.Join(", ", deps)) + "   [" + fi.Layer + "]");
                }
            }
            return feature + " (constructor injection)\n  " + string.Join("\n  ", lines);
        }
    }
}
