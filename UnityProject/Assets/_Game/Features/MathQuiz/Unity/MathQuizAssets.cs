using Game.Core;
using UnityEngine;

namespace Game.MathQuiz.Presentation
{
    /// <summary>
    /// The feature's single ASSET ENTRY POINT. Lives in Features/MathQuiz/Assets/Public/ and is the only
    /// MathQuiz asset that outside code (Bootstrap scene) may reference (ARCH080). It points to the
    /// feature's private assets. Swap skins/difficulty by pointing it elsewhere; delete the feature by
    /// deleting its folder after `test-gate.sh refs` shows no incoming references.
    /// Create via Assets > Create > Game > Math Quiz Assets, or Tools > Game > Prefabs > Rebuild MathQuiz.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Math Quiz Assets", fileName = "MathQuizAssets")]
    public sealed class MathQuizAssets : ScriptableObject
    {
        [Tooltip("Optional: QuizConfig.Default when empty.")]
        [OptionalReference] [SerializeField] private QuizConfigAsset config = null;

        [Tooltip("Optional: the UI is built by QuizViewLayout when empty. Usually a Variant of QuizView.generated.prefab.")]
        [OptionalReference] [SerializeField] private QuizView viewPrefab;

        internal QuizConfigAsset Config => config;
        internal QuizView ViewPrefab => viewPrefab;

#if UNITY_EDITOR
        /// <summary>Used by the prefab recipe to wire a freshly generated prefab.</summary>
        internal void EditorSetViewPrefab(QuizView prefab) => viewPrefab = prefab;
#endif
    }
}
