using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Entry point used by every gate: ArchitectureTests (dotnet + Unity), TestGate (check / run / hooks),
    /// git pre-commit, and the Unity Play Mode / build gates. One implementation, many callers.
    /// </summary>
    public static class ArchitectureScanner
    {
        /// <summary>All static rules over the whole project.</summary>
        public static List<RuleViolation> ScanAll(string repoRoot, DateTime? today = null)
        {
            var assets = new AssetRules(repoRoot);
            var docs = new DocRules(repoRoot);
            var result = ProjectRules(repoRoot, today);

            foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "UnityProject", "Assets", "_Game"), "*", SearchOption.AllDirectories))
                if (!file.EndsWith(".md", StringComparison.Ordinal))
                    result.AddRange(CheckOne(repoRoot, PathInfo.Normalize(file.Substring(repoRoot.Length)), assets, docs));
            foreach (var doc in docs.CheckedDocs()) // includes feature READMEs
                result.AddRange(docs.CheckAnchors(doc));

            return Finish(repoRoot, result, today);
        }

        /// <summary>Whole-project rules: asmdef graph, settings, third-party policy, generated overview.</summary>
        private static List<RuleViolation> ProjectRules(string repoRoot, DateTime? today)
        {
            var policy = ArchitecturePolicy.Load(repoRoot);
            var result = new List<RuleViolation>();
            result.AddRange(AsmdefRules.Check(repoRoot, policy));
            result.AddRange(UiRules.Check(repoRoot));
            result.AddRange(PlanRules.Check(repoRoot));
            result.AddRange(ArtRules.Check(repoRoot));
            result.AddRange(FileRules.Check(repoRoot));
            result.AddRange(new AssetRules(repoRoot).CheckProjectSettings());
            result.AddRange(ThirdPartyRules.Check(repoRoot, policy, AsmdefRules.Load(repoRoot), today ?? DateTime.UtcNow));
            result.AddRange(new DocRules(repoRoot).CheckGenerated());
            return result;
        }

        /// <summary>Applies valid, unexpired exceptions (ARCH095) and sorts.</summary>
        private static List<RuleViolation> Finish(string repoRoot, List<RuleViolation> result, DateTime? today)
        {
            var policy = ArchitecturePolicy.Load(repoRoot);
            if (policy.Exceptions.Count > 0)
            {
                var asmdefs = AsmdefRules.Load(repoRoot);
                string AssemblyOf(string path) => asmdefs
                    .Where(a => path == a.Path || path.StartsWith(a.Folder + "/", StringComparison.Ordinal))
                    .OrderByDescending(a => a.Folder.Length).Select(a => a.Name).FirstOrDefault();
                result = result.Where(v => !ThirdPartyRules.IsWaived(v, policy, AssemblyOf, today ?? DateTime.UtcNow)).ToList();
            }
            return Sorted(result);
        }

        /// <summary>
        /// Only the given files (fast path for hooks / pre-commit). Any changed .asmdef triggers the
        /// whole-graph asmdef rules; deleted files are skipped.
        /// </summary>
        /// <param name="projectRules">
        /// true (gate, pre-commit): also run whole-project rules (asmdef graph, third-party, generated overview), still milliseconds.
        /// false (after-edit hook): only per-file rules, so mid-task edits aren't nagged about the overview.
        /// </param>
        public static List<RuleViolation> ScanFiles(string repoRoot, IEnumerable<string> relativePaths, bool projectRules = true, DateTime? today = null)
        {
            var assets = new AssetRules(repoRoot);
            var docs = new DocRules(repoRoot);
            var paths = relativePaths.Select(PathInfo.Normalize).Distinct().ToList();
            var result = new List<RuleViolation>();

            if (projectRules)
                result.AddRange(ProjectRules(repoRoot, today));
            else if (paths.Any(p => p.EndsWith(".asmdef", StringComparison.Ordinal)))
                result.AddRange(AsmdefRules.Check(repoRoot));

            foreach (var p in paths.Where(p => File.Exists(Path.Combine(repoRoot, p))))
                result.AddRange(CheckOne(repoRoot, p, assets, docs));

            return Finish(repoRoot, result, today);
        }

        private static IEnumerable<RuleViolation> CheckOne(string root, string rel, AssetRules assets, DocRules docs) =>
            FileRules.CheckPath(rel).Concat(CheckContent(root, rel, assets, docs));

        private static IEnumerable<RuleViolation> CheckContent(string root, string rel, AssetRules assets, DocRules docs)
        {
            if (rel.EndsWith(".meta", StringComparison.Ordinal))
                return Enumerable.Empty<RuleViolation>();
            if (rel.EndsWith(".cs", StringComparison.Ordinal))
                return SourceRules.Check(rel, File.ReadAllText(Path.Combine(root, rel)));
            if (rel.EndsWith(".md", StringComparison.Ordinal))
                return DocRules.IsCheckedDoc(rel) ? docs.CheckAnchors(rel) : Enumerable.Empty<RuleViolation>();
            return assets.CheckFile(rel);
        }

        private static List<RuleViolation> Sorted(List<RuleViolation> list) =>
            list.GroupBy(v => v.ToString(), StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(v => v.RuleId, StringComparer.Ordinal).ThenBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line).ToList();

        /// <summary>Compact report: one line per violation + where to read about each rule once.</summary>
        public static string Format(IReadOnlyCollection<RuleViolation> violations, int max = 40)
        {
            var lines = violations.Take(max).Select(v => v.ToString()).ToList();
            if (violations.Count > max)
                lines.Add("... and " + (violations.Count - max) + " more");
            var ids = violations.Select(v => v.RuleId).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            lines.Add("Rule details: " + string.Join(", ", ids.Select(RuleCatalog.DocsLink)));
            return string.Join("\n", lines);
        }
    }
}
