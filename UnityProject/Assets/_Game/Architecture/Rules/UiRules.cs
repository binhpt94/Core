using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// ARCH101: every view is testable in isolation, by convention (no manifest to keep in sync):
    ///   Features/F/Unity/Views/V/V.cs            the MonoBehaviour implementing I*View
    ///   Features/F/Unity/Views/V/VLayout.cs      its layout recipe (optional; the prefab may be hand-made)
    ///   Features/F/DevTools/Fixtures/VFixtures.cs  view states incl. a "smoke" fixture (C#, typed)
    ///   Features/F/Editor/Views/VCases.cs        how to instantiate V and apply a fixture (Editor)
    /// The folder is what lets the test gate map a changed file or prefab to exactly one view.
    /// </summary>
    public static class UiRules
    {
        private static readonly Regex ViewClass = new Regex(@"\bclass\s+(\w+)\s*:\s*MonoBehaviour\s*,[^{]*\bI\w+View\b");

        public sealed class ViewInfo
        {
            public string Feature;
            public string Name;
            public string SourcePath;   // repo-relative
            public string Folder => PathInfo.GameRoot + "Features/" + Feature + "/Unity/Views/" + Name;
            public string FixturesPath => PathInfo.GameRoot + "Features/" + Feature + "/DevTools/Fixtures/" + Name + "Fixtures.cs";
            public string CasesPath => PathInfo.GameRoot + "Features/" + Feature + "/Editor/Views/" + Name + "Cases.cs";
            public string BaselinePath => PathInfo.GameRoot + "Features/" + Feature + "/Tests/Layout/" + Name + ".layout.txt";
            public string Id => Feature + "/" + Name;
        }

        /// <summary>Views found in Features/*/Unity (wherever they are; ARCH101 reports misplaced ones).</summary>
        public static List<ViewInfo> FindViews(string repoRoot)
        {
            var result = new List<ViewInfo>();
            string features = Path.Combine(repoRoot, "UnityProject", "Assets", "_Game", "Features");
            if (!Directory.Exists(features))
                return result;
            foreach (var file in Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories))
            {
                string rel = PathInfo.Normalize(file.Substring(repoRoot.Length));
                var info = PathInfo.Of(rel);
                if (info.Layer != PathLayer.Unity)
                    continue;
                var m = ViewClass.Match(SourceText.StripComments(File.ReadAllText(file)));
                if (m.Success)
                    result.Add(new ViewInfo { Feature = info.Feature, Name = m.Groups[1].Value, SourcePath = rel });
            }
            return result.OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
        }

        public static IEnumerable<RuleViolation> Check(string repoRoot)
        {
            foreach (var v in FindViews(repoRoot))
            {
                string expected = v.Folder + "/" + v.Name + ".cs";
                if (v.SourcePath != expected)
                {
                    yield return new RuleViolation("ARCH101", v.SourcePath, 0, "view " + v.Name + " must live in " + expected + " (the gate maps changes to views by folder)");
                    continue;
                }

                string fixtures = Path.Combine(repoRoot, v.FixturesPath);
                if (!File.Exists(fixtures))
                    yield return new RuleViolation("ARCH101", v.SourcePath, 0, "view " + v.Name + " has no fixtures: create " + v.FixturesPath + " with a \"smoke\" fixture");
                else if (!File.ReadAllText(fixtures).Contains("\"smoke\"") && !File.ReadAllText(fixtures).Contains("FixtureTags.Smoke"))
                    yield return new RuleViolation("ARCH101", v.FixturesPath, 0, "no fixture named \"smoke\" (the minimal state every gate renders)");

                if (!File.Exists(Path.Combine(repoRoot, v.CasesPath)))
                    yield return new RuleViolation("ARCH101", v.SourcePath, 0, "view " + v.Name + " has no view cases: create " + v.CasesPath + " (ViewCases<I..View>)");
            }
        }
    }
}
