using System.Collections.Generic;

namespace Game.MathQuiz.Domain
{
    /// <summary>
    /// CONTENT, not design: what kind of enemy walks the lane (slime, bat, ...) and its numbers. Swapping the archetype is the
    /// "Sói 3 skill → Hổ 2 skill" kind of change: it needs no approval as long as the design intents still hold
    /// (<see cref="ArenaDesign"/>, checked by the design-lint tests that [Covers] the story requirements of the plan).
    /// The art slot id is the stable link to presentation: art can change without the gameplay knowing.
    /// </summary>
    internal sealed class EnemyArchetype
    {
        public string Id { get; }
        public string DisplayName { get; }
        public int SpeedMin { get; }
        public int SpeedMax { get; }
        public int HopPercent { get; }
        public int RagePercent { get; }
        /// <summary>Stable art slot id ("Feature.Name"); presentation resolves it, gameplay never loads art.</summary>
        public string ArtSlot { get; }

        public EnemyArchetype(string id, string displayName, int speedMin, int speedMax, int hopPercent, int ragePercent, string artSlot)
        {
            Id = id;
            DisplayName = displayName;
            SpeedMin = speedMin;
            SpeedMax = speedMax;
            HopPercent = hopPercent;
            RagePercent = ragePercent;
            ArtSlot = artSlot;
        }
    }

    /// <summary>Content catalog. Slime ships; Bat is a candidate variant kept to show what the design lint says about it.</summary>
    internal static class EnemyArchetypes
    {
        public static EnemyArchetype Slime { get; } = new EnemyArchetype("slime", "Slime", 4, 8, 25, 175, MathQuizArtSlots.SlimeId);
        public static EnemyArchetype Bat { get; } = new EnemyArchetype("bat", "Dơi", 10, 16, 10, 150, MathQuizArtSlots.BatId);

        /// <summary>The archetype the game uses (ArenaConfig.Default is built from it).</summary>
        public static EnemyArchetype Active => Slime;

        public static IReadOnlyList<EnemyArchetype> All { get; } = new[] { Slime, Bat };
    }
}
