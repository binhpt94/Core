using System.Linq;
using Game.Architecture.Rules;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Bootstrap.EditorTools
{
    /// <summary>
    /// Makes Unity itself "go red" (for humans, Unity MCP and agents alike):
    /// 1. on import: changed files are checked and errors appear in the Console;
    /// 2. before Play Mode: entering Play Mode is CANCELLED while violations exist;
    /// 3. before a build: the build FAILS.
    /// CI and tests run the same rules, so skipping the Editor doesn't skip the rules.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeGate
    {
        private const string SkipKey = "Game.Validation.SkipPlayModeGate";
        private const string ToggleMenu = "Tools/Game/Validation/Block Play Mode On Violations";

        static PlayModeGate()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Batch mode (CI / test runner) has its own gates; never block it from here.
            if (change != PlayModeStateChange.ExitingEditMode || Application.isBatchMode || EditorPrefs.GetBool(SkipKey, false))
                return;

            var docs = new System.Collections.Generic.List<string>();
            var errors = ProjectValidator.Run(includeBuildScenes: false, documentationWarnings: docs);
            if (errors.Count > 0)
            {
                EditorApplication.isPlaying = false;
                ProjectValidator.Log(errors, "Play Mode blocked");
                return;
            }

            if (docs.Count > 0)
                Debug.LogWarning("[Validation] System map / docs out of date (" + docs.Count + "). Not blocking Play Mode. " +
                                 "Fix: Tools > Game > Regenerate System Map, then commit Docs/Architecture.\n" + string.Join("\n", docs.Take(10)));
        }

        [MenuItem("Tools/Game/Regenerate System Map")]
        private static void RegenerateSystemMap()
        {
            var changed = ArchitectureDocs.WriteAll(ProjectValidator.ProjectRoot);
            var parse = ArchitectureGraph.Build(ProjectValidator.ProjectRoot).ParseErrors;
            foreach (var e in parse)
                Debug.LogError("[Validation] " + e);
            Debug.Log(changed.Count == 0
                ? "[Validation] System map already up to date."
                : "[Validation] Regenerated " + string.Join(", ", changed) + ". Commit Docs/Architecture.");
        }

        [MenuItem(ToggleMenu)]
        private static void Toggle() => EditorPrefs.SetBool(SkipKey, !EditorPrefs.GetBool(SkipKey, false));

        [MenuItem(ToggleMenu, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(ToggleMenu, !EditorPrefs.GetBool(SkipKey, false));
            return true;
        }

        [MenuItem("Tools/Game/Validation/Validate Project")]
        private static void ValidateNow()
        {
            var errors = ProjectValidator.Run(includeBuildScenes: true);
            if (errors.Count == 0)
                Debug.Log("[Validation] OK: no violations.");
            else
                ProjectValidator.Log(errors, "Validation failed");
        }
    }

    /// <summary>Checks imported/moved files right away (static rules + prefab references).</summary>
    internal sealed class ImportValidation : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var paths = imported.Concat(moved)
                .Where(p => p.StartsWith(ProjectValidator.GameFolder + "/", System.StringComparison.Ordinal))
                .ToList();
            if (paths.Count == 0)
                return;

            // projectRules: false → per-file rules only (fast on every import, and never nags about the system map).
            var errors = ArchitectureScanner.ScanFiles(ProjectValidator.ProjectRoot, paths.Select(Layout.FromUnity).ToList(), projectRules: false)
                .Where(v => !RuleCatalog.IsDocumentationRule(v.RuleId))
                .Select(v => v.ToString()).ToList();
            foreach (var path in paths.Where(p => p.EndsWith(".prefab", System.StringComparison.Ordinal)))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    errors.AddRange(ReferenceValidator.ValidateHierarchy(prefab, path));
            }

            if (errors.Count > 0)
                ProjectValidator.Log(errors, "Imported assets violate architecture rules");
        }
    }

    /// <summary>Fails player builds while violations exist.</summary>
    internal sealed class BuildGate : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            // ARCH102 (build side): dev tools (fixtures, scenarios, cheats) compile only in the Editor and with GAME_DEVTOOLS.
            // A release (non-development) build must not define it.
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            var target = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
            string defines = PlayerSettings.GetScriptingDefineSymbols(target);
            if (!development && defines.Split(';').Any(d => d.Trim() == "GAME_DEVTOOLS"))
                throw new BuildFailedException("ARCH102 release build defines GAME_DEVTOOLS (cheats/fixtures would ship). Remove it from Player Settings > Scripting Define Symbols, or make a Development build.");

            var errors = ProjectValidator.Run(includeBuildScenes: true);
            if (errors.Count == 0)
                return;

            ProjectValidator.Log(errors, "Build blocked");
            throw new BuildFailedException("Architecture validation failed (" + errors.Count + " violation(s)). See Console / Docs/RULES.md. First: " + errors[0]);
        }
    }
}
