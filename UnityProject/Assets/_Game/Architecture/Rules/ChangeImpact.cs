using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>What a changed file can break, decided by its ROLE (path + the rules that constrain that path).</summary>
    public enum ChangeRole
    {
        /// <summary>Docs: static doc rules only.</summary>
        Docs,
        /// <summary>Pure C# (Core, Contracts, Logic, DevTools, pure tests, rule engine): dotnet test.</summary>
        Pure,
        /// <summary>One view's code, recipe, prefab, cases or baseline: layout gate of that view only.</summary>
        View,
        /// <summary>What every view of the feature shows (view models, texts, presenter, fixtures, feature assets).</summary>
        FeatureViews,
        /// <summary>Composition, installers, scenes, settings, asmdefs: Unity tests by assembly graph (incl. PlayMode smoke).</summary>
        Integration,
        /// <summary>Shared UI tooling (Core/Editor): every view.</summary>
        AllViews,
        /// <summary>Art bound to a slot (or the slot binding / contract): the art gate of those slots only.</summary>
        Art,
        /// <summary>Unrecognised: run everything (fail-closed).</summary>
        Unknown
    }

    public sealed class FileImpact
    {
        public string Path { get; }
        public IReadOnlyList<ChangeRole> Roles { get; }
        /// <summary>View ids ("Feature/View") whose layout gate must run.</summary>
        public IReadOnlyList<string> Views { get; }
        public string Reason { get; }
        /// <summary>Art slot ids whose art gate must run.</summary>
        public IReadOnlyList<string> Slots { get; }

        public FileImpact(string path, IReadOnlyList<ChangeRole> roles, IReadOnlyList<string> views, string reason, IReadOnlyList<string> slots = null)
        {
            Path = path;
            Roles = roles;
            Views = views;
            Reason = reason;
            Slots = slots ?? new string[0];
        }

        public bool Has(ChangeRole role) => Roles.Contains(role);

        public override string ToString() => Path + " -> " + string.Join("+", Roles) + (Views.Count > 0 ? " [" + string.Join(", ", Views) + "]" : "") +
                                             (Slots.Count > 0 ? " {" + string.Join(", ", Slots) + "}" : "") + " (" + Reason + ")";
    }

    /// <summary>
    /// Maps changed files to the smallest set of gates that can catch what they may break. It is exact (not a
    /// heuristic over diffs) because the rules make the roles exact: views hold no logic (ARCH057/070), static
    /// layout lives only in the view's recipe or prefab (ARCH100), views live in their own folder (ARCH101).
    /// Unknown paths are fail-closed (full run). The nightly full run is the safety net: a bug it catches that a
    /// scoped run missed is a hole in this table, to be fixed here, not by widening every run.
    /// </summary>
    public sealed class ChangeImpact
    {
        private static readonly Regex ScriptRef = new Regex(@"m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*([0-9a-f]{32})");
        private static readonly Regex SourcePrefab = new Regex(@"m_SourcePrefab:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})");
        private static readonly Regex MetaGuid = new Regex(@"^guid:\s*([0-9a-f]{32})", RegexOptions.Multiline);

        private readonly string _root;
        private readonly List<UiRules.ViewInfo> _views;
        private Dictionary<string, string> _guidToPath;

        public ChangeImpact(string repoRoot)
        {
            _root = repoRoot;
            _views = UiRules.FindViews(repoRoot);
        }

        public IReadOnlyList<UiRules.ViewInfo> Views => _views;

        public FileImpact Classify(string relativePath)
        {
            string p = PathInfo.Normalize(relativePath);
            var info = PathInfo.Of(p);
            string G = PathInfo.GameRoot;

            if (p.EndsWith(".meta", StringComparison.Ordinal))
                return Classify(p.Substring(0, p.Length - 5));
            if (p.StartsWith("plan/", StringComparison.Ordinal))
                return Make(p, "product plan / proposal (ARCH110/111 static checks; roadmap status recomputes)", ChangeRole.Docs);
            if (p.EndsWith(".md", StringComparison.Ordinal))
                return Make(p, "documentation", ChangeRole.Docs);
            if (p.StartsWith("tools/", StringComparison.Ordinal) || p.StartsWith("LogicTests/", StringComparison.Ordinal) || info.Layer == PathLayer.Architecture
                || p.StartsWith(G + "Tests/Architecture/", StringComparison.Ordinal))
                return Make(p, "rule engine / gate / pure test project", ChangeRole.Pure);
            if (p.StartsWith(".claude/", StringComparison.Ordinal) || p.StartsWith(".github/", StringComparison.Ordinal)
                || p == "ArchitecturePolicy.txt" || p == ".gitignore" || p == ".editorconfig" || p == "CLAUDE.md" || p == "AGENTS.md")
                return Make(p, "repo configuration (static rules)", ChangeRole.Pure);

            // One view: its folder, its cases, its fixtures, its baseline.
            var view = _views.FirstOrDefault(v =>
                p.StartsWith(v.Folder + "/", StringComparison.Ordinal) || p == v.CasesPath || p == v.BaselinePath);
            if (view != null)
                return Make(p, "view " + view.Id + " (code, layout recipe, cases or baseline)", new[] { view.Id }, ChangeRole.View);
            view = _views.FirstOrDefault(v => p == v.FixturesPath);
            if (view != null)
                return Make(p, "fixtures of " + view.Id, new[] { view.Id }, ChangeRole.Pure, ChangeRole.View);

            // Art: binding assets, files bound (directly or through one prefab) to a slot, slot catalogs, shared art code.
            var art = ArtSlotsOf(p);
            if (art.Count > 0)
                return new FileImpact(p, new[] { ChangeRole.Art }, new string[0], "art of slot(s) " + string.Join(", ", art) + " (via GUID)", art);
            if (p.EndsWith("ArtSlots.cs", StringComparison.Ordinal) && p.Contains("/Logic/Domain/"))
            {
                var slots = ArtRules.Declarations(_root).Where(d => d.File == p).Select(d => d.Id).ToList();
                return new FileImpact(p, new[] { ChangeRole.Pure, ChangeRole.Art }, new string[0], "art slot contracts of " + info.Feature, slots);
            }
            if (p.StartsWith(G + "Core/Runtime/Art/", StringComparison.Ordinal) || p.StartsWith(G + "Core/Unity/", StringComparison.Ordinal) || p.StartsWith(G + "Core/Editor/Art/", StringComparison.Ordinal))
                return new FileImpact(p, new[] { p.Contains("/Runtime/") ? ChangeRole.Pure : ChangeRole.Art, ChangeRole.Art }.Distinct().ToArray(), AllViews(), "shared art-slot code: every slot (and views using graybox)",
                    ArtRules.Declarations(_root).Select(d => d.Id).ToList());

            if (p.EndsWith(".asmdef", StringComparison.Ordinal) || p.EndsWith(".asmref", StringComparison.Ordinal))
                return Make(p, "assembly definition", ChangeRole.Pure, ChangeRole.Integration);

            switch (info.Layer)
            {
                case PathLayer.Core:
                    return Make(p, "pure core", ChangeRole.Pure);
                case PathLayer.Contracts:
                    return Make(p, "contracts", ChangeRole.Pure);
                case PathLayer.Logic:
                    // UseCases = presenters, view interfaces/models, texts: what the views of this feature show.
                    // Domain/Simulation: gameplay; covered by pure tests + golden replays (scenario fixtures are L2-only).
                    return p.Contains("/Logic/UseCases/")
                        ? Make(p, "presenter / view models / texts of " + info.Feature, FeatureViews(info.Feature), ChangeRole.Pure, ChangeRole.FeatureViews)
                        : Make(p, "gameplay logic", ChangeRole.Pure);
                case PathLayer.DevTools:
                    return Make(p, "scenarios / dev tools of " + info.Feature, FeatureViews(info.Feature), ChangeRole.Pure, ChangeRole.FeatureViews);
                case PathLayer.Tests:
                    if (p.StartsWith(G + "Core/Editor/Tests/", StringComparison.Ordinal))
                        return Make(p, "layout gate tests", AllViews(), ChangeRole.AllViews);
                    if (p.Contains("/Tests/Unity/") || IsUnityTestAssembly(p))
                        return Make(p, "Unity tests", ChangeRole.Integration);
                    return Make(p, "pure tests", ChangeRole.Pure);
                case PathLayer.Editor:
                    if (info.Feature == null)
                        return Make(p, "shared view tooling (stage, probe, preview)", AllViews(), ChangeRole.AllViews);
                    return Make(p, "editor tools of " + info.Feature, ChangeRole.Integration);
                case PathLayer.Unity:
                    return Make(p, "feature wiring (installer, module, assets class)", FeatureViews(info.Feature), ChangeRole.Integration, ChangeRole.FeatureViews);
                case PathLayer.FeatureAssets:
                    return ClassifyAsset(p, info.Feature);
                case PathLayer.Bootstrap:
                case PathLayer.Scenes:
                    return Make(p, "composition / scene", ChangeRole.Integration);
            }

            if (p.StartsWith("Packages/", StringComparison.Ordinal) || p.StartsWith("ProjectSettings/", StringComparison.Ordinal))
                return Make(p, "packages / project settings", ChangeRole.Integration, ChangeRole.AllViews);

            return Make(p, "unrecognised path (fail-closed)", ChangeRole.Unknown);
        }

        private FileImpact ClassifyAsset(string p, string feature)
        {
            if (p.EndsWith(".prefab", StringComparison.Ordinal))
            {
                var owner = ViewOfPrefab(p, 0);
                if (owner != null)
                    return Make(p, "prefab of " + owner.Id + " (via script GUID)", new[] { owner.Id }, ChangeRole.View);
                return Make(p, "prefab not resolvable to one view (no .meta / not a view)", FeatureViews(feature), ChangeRole.FeatureViews, ChangeRole.Integration);
            }
            // Config, sprites, fonts, materials: may change what any view of the feature shows, and the game.
            return Make(p, "feature asset", FeatureViews(feature), ChangeRole.FeatureViews, ChangeRole.Integration);
        }

        /// <summary>Slots whose binding is this file, or binds it directly, or binds a prefab that references it.</summary>
        private List<string> ArtSlotsOf(string p)
        {
            var bindings = ArtRules.Bindings(_root);
            var direct = bindings.Where(b => b.Path == p).Select(b => b.SlotId).ToList();
            if (direct.Count > 0 || !p.StartsWith("Assets/", StringComparison.Ordinal))
                return direct;
            string guid = GuidOf(p);
            if (guid == null)
                return new List<string>();
            var hits = new List<string>();
            var map = GuidMap();
            foreach (var b in bindings)
                foreach (var bound in new[] { b.CandidateGuid, b.FinalGuid }.Where(g => g.Length > 0))
                {
                    if (bound == guid)
                        hits.Add(b.SlotId);
                    else if (map.TryGetValue(bound, out var prefab) && File.Exists(Path.Combine(_root, prefab)) && File.ReadAllText(Path.Combine(_root, prefab)).Contains("guid: " + guid))
                        hits.Add(b.SlotId); // sprite/clip/material used by the bound prefab
                }
            return hits.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private string GuidOf(string p)
        {
            string meta = Path.Combine(_root, p + ".meta");
            if (!File.Exists(meta))
                return null;
            var m = MetaGuid.Match(File.ReadAllText(meta));
            return m.Success ? m.Groups[1].Value : null;
        }

        private UiRules.ViewInfo ViewOfPrefab(string prefab, int depth)
        {
            string full = Path.Combine(_root, prefab);
            if (depth > 8 || !File.Exists(full))
                return null;
            string yaml = File.ReadAllText(full);
            var guids = GuidMap();
            foreach (Match m in ScriptRef.Matches(yaml))
                if (guids.TryGetValue(m.Groups[1].Value, out var script))
                {
                    var v = _views.FirstOrDefault(x => x.SourcePath == script);
                    if (v != null)
                        return v;
                }
            foreach (Match m in SourcePrefab.Matches(yaml)) // Prefab Variant → its base
                if (guids.TryGetValue(m.Groups[1].Value, out var source))
                {
                    var v = ViewOfPrefab(source, depth + 1);
                    if (v != null)
                        return v;
                }
            return null;
        }

        private Dictionary<string, string> GuidMap()
        {
            if (_guidToPath != null)
                return _guidToPath;
            _guidToPath = new Dictionary<string, string>(StringComparer.Ordinal);
            string assets = Path.Combine(_root, "Assets");
            if (Directory.Exists(assets))
                foreach (var meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
                {
                    var m = MetaGuid.Match(File.ReadAllText(meta));
                    if (m.Success)
                        _guidToPath[m.Groups[1].Value] = PathInfo.Normalize(meta.Substring(_root.Length, meta.Length - _root.Length - 5));
                }
            return _guidToPath;
        }

        private bool IsUnityTestAssembly(string p)
        {
            for (var dir = Path.GetDirectoryName(Path.Combine(_root, p)); dir != null && dir.Length > _root.Length; dir = Path.GetDirectoryName(dir))
            {
                var asmdef = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.asmdef").FirstOrDefault() : null;
                if (asmdef != null)
                    return !Regex.IsMatch(File.ReadAllText(asmdef), "\"noEngineReferences\"\\s*:\\s*true");
            }
            return false;
        }

        private string[] FeatureViews(string feature) => _views.Where(v => v.Feature == feature).Select(v => v.Id).ToArray();

        private string[] AllViews() => _views.Select(v => v.Id).ToArray();

        private static FileImpact Make(string p, string reason, params ChangeRole[] roles) => new FileImpact(p, roles, new string[0], reason);

        private static FileImpact Make(string p, string reason, string[] views, params ChangeRole[] roles) => new FileImpact(p, roles, views, reason);
    }
}
