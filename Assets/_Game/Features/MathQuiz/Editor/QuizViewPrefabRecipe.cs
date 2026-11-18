using Game.MathQuiz.Presentation;
using UnityEditor;
using UnityEngine;

namespace Game.MathQuiz.EditorTools
{
    /// <summary>
    /// PREFAB RECIPE: agents change UI structure in code (QuizViewLayout), never by editing prefab YAML.
    /// This turns the recipe into the base prefab; artists skin a Prefab Variant of it.
    ///
    ///   QuizViewLayout (code) ──Create Base Prefab──► Assets/Private/QuizView.generated.prefab  (never hand-edit)
    ///                                                     └── Variant, e.g. QuizView_SkinA.prefab (artists)
    ///   MathQuizAssets (Assets/Public) → points to the generated prefab or to a Variant.
    ///
    /// Batch mode (agents / CI):
    ///   Unity -batchmode -quit -projectPath . -executeMethod Game.MathQuiz.EditorTools.QuizViewPrefabRecipe.CreateBasePrefab
    /// </summary>
    public static class QuizViewPrefabRecipe
    {
        public const string GeneratedPrefabPath = "Assets/_Game/Features/MathQuiz/Assets/Private/QuizView.generated.prefab";
        public const string AssetsEntryPath = "Assets/_Game/Features/MathQuiz/Assets/Public/MathQuizAssets.asset";

        [MenuItem("Tools/Game/Prefabs/MathQuiz: Create Base Prefab")]
        public static void CreateBasePrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedPrefabPath) != null)
            {
                Debug.Log("[Recipe] " + GeneratedPrefabPath + " already exists. Use 'Force Rebuild' after changing the recipe.");
                EnsureAssetsEntry();
                return;
            }
            Build();
        }

        /// <summary>
        /// Regenerates the base from scratch. Objects get new file IDs, so Variant overrides on objects
        /// the recipe recreated may be lost: re-check Variants afterwards (the ARCH072 validator reports
        /// broken references). Prefer small, additive recipe changes once artists have skinned Variants.
        /// </summary>
        [MenuItem("Tools/Game/Prefabs/MathQuiz: Force Rebuild Base Prefab")]
        public static void ForceRebuild()
        {
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog("Rebuild base prefab",
                    "Regenerate " + GeneratedPrefabPath + " from the recipe?\nVariant overrides on recreated objects may be lost.", "Rebuild", "Cancel"))
                return;
            Build();
        }

        private static void Build()
        {
            var view = QuizViewLayout.Create(null);
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(view.gameObject, GeneratedPrefabPath);
                Debug.Log("[Recipe] Saved " + GeneratedPrefabPath);
                EnsureAssetsEntry(prefab.GetComponent<QuizView>());
            }
            finally
            {
                Object.DestroyImmediate(view.gameObject);
            }
        }

        private static void EnsureAssetsEntry(QuizView generated = null)
        {
            var entry = AssetDatabase.LoadAssetAtPath<MathQuizAssets>(AssetsEntryPath);
            if (entry == null)
            {
                entry = ScriptableObject.CreateInstance<MathQuizAssets>();
                AssetDatabase.CreateAsset(entry, AssetsEntryPath);
                Debug.Log("[Recipe] Created " + AssetsEntryPath + " (drag it onto GameRoot > Math Quiz Assets)");
            }

            if (entry.ViewPrefab == null)
            {
                if (generated == null)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratedPrefabPath); // no ?. on UnityEngine.Object
                    if (prefab != null)
                        generated = prefab.GetComponent<QuizView>();
                }
                if (generated != null)
                {
                    entry.EditorSetViewPrefab(generated);
                    EditorUtility.SetDirty(entry);
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
