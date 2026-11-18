using System.Collections.Generic;
using Game.Core;

namespace Game.MathQuiz.Domain
{
    /// <summary>
    /// Art slots of this feature: the measurable promise art must keep, in the units the view uses (canvas units for uGUI).
    /// Gameplay refers to the slot id only. The graybox is GENERATED from the contract, so it always conforms and gameplay
    /// can ship before any art exists; real art is bound to the slot later and checked by the art gate (Unity EditMode).
    /// </summary>
    internal sealed class MathQuizArtSlots : IArtSlotCatalog
    {
        public const string SlimeId = "MathQuiz.Slime";
        public const string BatId = "MathQuiz.Bat";

        /// <summary>Lane marker: fits the lane band (0.15–0.85 of a 110–140 high lane), readable, centred pivot.</summary>
        public static ArtSlotContract Slime { get; } = new ArtSlotContract(SlimeId, ArtSlotKind.UiImage,
            minWidth: 32, maxWidth: 72, minHeight: 60, maxHeight: 110, pivotXPercent: 50, pivotYPercent: 50, pivotTolerancePercent: 10,
            tickRate: ArenaConfig.TickRate,
            // Optional: a static image is fine; IF the art animates a hop, it must land inside the hop window.
            actions: new[] { new ArtAction("Hop", marker: "land", minTick: 2, maxTick: 6, minSpeedPercent: 80, maxSpeedPercent: 125, required: false) });

        public static ArtSlotContract Bat { get; } = new ArtSlotContract(BatId, ArtSlotKind.UiImage,
            minWidth: 40, maxWidth: 96, minHeight: 40, maxHeight: 90, pivotXPercent: 50, pivotYPercent: 50, pivotTolerancePercent: 10,
            tickRate: ArenaConfig.TickRate, actions: new ArtAction[0]);

        public IReadOnlyList<ArtSlotContract> Slots => new[] { Slime, Bat };
    }
}
