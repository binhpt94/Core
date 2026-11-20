using System.Collections.Generic;
using System.Linq;
using Game.Core.DevTools;
using Game.Core.EditorTools;
using Game.MathQuiz.Domain;
using Game.MathQuiz.DevTools;
using Game.MathQuiz.Presentation;
using Game.MathQuiz.UseCases;
using UnityEditor;
using UnityEngine;

namespace Game.MathQuiz.EditorTools
{
    /// <summary>
    /// QuizView in isolation (layout gate, View Preview window). Tests the prefab the game SHIPS (asset entry → Variant
    /// or generated base); falls back to the layout recipe when no prefab exists yet. Fixtures follow the shipped config,
    /// so raising MaxOperand automatically tests longer texts.
    /// </summary>
    internal sealed class QuizViewCases : ViewCases<IQuizView>
    {
        protected override string Feature => "MathQuiz";
        protected override string View => "QuizView";

        protected override IReadOnlyList<ViewFixture<IQuizView>> Fixtures() => QuizViewFixtures.All(ShippedConfig());

        /// <summary>The slimes walking the lane (QuizView names each spawned marker "Slime&lt;id&gt;"; the inactive template is skipped).</summary>
        public override IReadOnlyList<ArtPlacement> ArtPlacements => new[]
        {
            new ArtPlacement(MathQuizArtSlots.SlimeId, "smoke", root => root.GetComponentsInChildren<RectTransform>(false)
                .Where(t => t.name.StartsWith("Slime", System.StringComparison.Ordinal) && t.name != "SlimeTemplate"))
        };

        protected override IQuizView CreateView(Transform parent, out GameObject root)
        {
            var entry = AssetDatabase.LoadAssetAtPath<MathQuizAssets>(QuizViewPrefabRecipe.AssetsEntryPath);
            QuizView view = entry != null && entry.ViewPrefab != null
                ? Object.Instantiate(entry.ViewPrefab, parent)
                : QuizViewLayout.Create(parent);
            root = view.gameObject;
            return view;
        }

        private static QuizConfig ShippedConfig()
        {
            var entry = AssetDatabase.LoadAssetAtPath<MathQuizAssets>(QuizViewPrefabRecipe.AssetsEntryPath);
            if (entry == null || entry.Config == null)
                return QuizConfig.Default;
            var result = entry.Config.ToConfig();
            return result.IsSuccess ? result.Value : QuizConfig.Default;
        }
    }
}
