using System;
using Game.Core;
using Game.MathQuiz.Domain;

namespace Game.MathQuiz.UseCases
{
    /// <summary>
    /// The arena as a DESIGNER edits it (seconds, units per second: QuizConfigAsset in the Inspector) and the one conversion to
    /// simulation units (ticks, per tick). Shared by the ScriptableObject and by the design-lint tests, so the lint checks the
    /// numbers the game really ships, not only the code defaults.
    /// </summary>
    internal sealed class DesignerArena
    {
        public int BaseLives { get; }
        public float SpawnMinSeconds { get; }
        public float SpawnMaxSeconds { get; }
        public int SpeedMinPerSecond { get; }
        public int SpeedMaxPerSecond { get; }
        public int HopPercent { get; }

        public DesignerArena(int baseLives, float spawnMinSeconds, float spawnMaxSeconds, int speedMinPerSecond, int speedMaxPerSecond, int hopPercent)
        {
            BaseLives = baseLives;
            SpawnMinSeconds = spawnMinSeconds;
            SpawnMaxSeconds = spawnMaxSeconds;
            SpeedMinPerSecond = speedMinPerSecond;
            SpeedMaxPerSecond = speedMaxPerSecond;
            HopPercent = hopPercent;
        }

        /// <summary>The Inspector defaults of QuizConfigAsset (equal to ArenaConfig.Default once converted).</summary>
        public static DesignerArena Default { get; } = new DesignerArena(3, 2f, 4f, 80, 160, 25);

        /// <summary>Rounding happens HERE, once (same as Unity's Mathf.RoundToInt: to even).</summary>
        public Result<ArenaConfig> ToArenaConfig()
        {
            int Ticks(float seconds) => Math.Max(1, (int)Math.Round(seconds * ArenaConfig.TickRate));
            int PerTick(int perSecond) => Math.Max(1, perSecond / ArenaConfig.TickRate);
            return ArenaConfig.Create(BaseLives, Ticks(SpawnMinSeconds), Ticks(SpawnMaxSeconds), PerTick(SpeedMinPerSecond), PerTick(SpeedMaxPerSecond), HopPercent);
        }
    }
}
