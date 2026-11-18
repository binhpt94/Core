using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Bootstrap.EditorTools
{
    /// <summary>
    /// Everything the Editor can check without running the game:
    /// static rules (shared engine) + required references in prefabs, ScriptableObjects and scenes.
    /// </summary>
    public static class ProjectValidator
    {
        public const string GameFolder = "Assets/_Game";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        /// <summary>
        /// Violations that must stop Play Mode / builds. Documentation rules (stale system map, doc anchors) are
        /// excluded on purpose (see <see cref="RuleCatalog.IsDocumentationRule"/>); they are reported by
        /// the optional documentationWarnings list and enforced by hooks, pre-commit and CI.
        /// </summary>
        public static List<string> Run(bool includeBuildScenes, List<string> documentationWarnings = null)
        {
            var all = ArchitectureScanner.ScanAll(ProjectRoot); // one scan for both lists
            documentationWarnings?.AddRange(all.Where(v => RuleCatalog.IsDocumentationRule(v.RuleId)).Select(v => v.ToString()));
            var errors = all
                .Where(v => !RuleCatalog.IsDocumentationRule(v.RuleId))
                .Select(v => v.ToString()).ToList();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { GameFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    errors.AddRange(ReferenceValidator.ValidateHierarchy(prefab, path));
            }

            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { GameFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (asset != null)
                    errors.AddRange(ReferenceValidator.ValidateObject(asset, path));
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
                errors.AddRange(ValidateScene(SceneManager.GetSceneAt(i)));

            if (includeBuildScenes)
            {
                foreach (var entry in EditorBuildSettings.scenes.Where(s => s.enabled))
                {
                    if (SceneManager.GetSceneByPath(entry.path).isLoaded)
                        continue;
                    var scene = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Additive);
                    errors.AddRange(ValidateScene(scene));
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            return errors.Distinct().ToList();
        }

        private static IEnumerable<string> ValidateScene(Scene scene)
        {
            if (!scene.isLoaded)
                yield break;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var error in ReferenceValidator.ValidateHierarchy(root, scene.path))
                    yield return error;
        }

        public static void Log(IReadOnlyCollection<string> errors, string header)
        {
            foreach (var e in errors.Take(50))
                Debug.LogError("[Validation] " + e);
            Debug.LogError("[Validation] " + header + ": " + errors.Count + " violation(s). Details: docs/RULES.md");
        }
    }
}
