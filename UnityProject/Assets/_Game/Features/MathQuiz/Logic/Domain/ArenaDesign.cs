using System.Collections.Generic;

namespace Game.MathQuiz.Domain
{
    /// <summary>One experience goal of the story, as a check over content numbers (no art, no Unity, integers only).</summary>
    internal sealed class DesignIntent
    {
        public string Name { get; }
        public string Goal { get; }
        private readonly System.Func<ArenaConfig, string> _violation;

        public DesignIntent(string name, string goal, System.Func<ArenaConfig, string> violation)
        {
            Name = name;
            Goal = goal;
            _violation = violation;
        }

        /// <summary>null = holds; otherwise why not, with the numbers.</summary>
        public string Violation(ArenaConfig config) => _violation(config);
    }

    /// <summary>
    /// "Design lint": the story's experience goals for the arena, written as computable constraints. A content swap
    /// (another archetype, other numbers) is accepted automatically when every intent still holds; when one breaks,
    /// the gate is red and THAT is when a human decides (change the content, or change the intent in the plan).
    /// </summary>
    internal static class ArenaDesign
    {
        public const int BreatherSeconds = 5;
        /// <summary>Max lane distance per tick while raging, in lane units (1000 = whole lane): 25 = 2.5%.</summary>
        public const int MaxReadableSpeed = 25;

        /// <summary>Earliest tick a slime can reach the base if the player never answers wrong (no rage, no hop).</summary>
        public static int FirstContactTicks(ArenaConfig c) =>
            c.SpawnMinTicks / 2 + (ArenaConfig.LaneLength + c.SpeedMax - 1) / c.SpeedMax;

        /// <summary>Fastest movement per tick while raging.</summary>
        public static int RagingSpeed(ArenaConfig c) => c.SpeedMax * c.RagePercent / 100;

        public static IReadOnlyList<DesignIntent> Intents { get; } = new[]
        {
            new DesignIntent("breather", "a player who answers right gets at least " + BreatherSeconds + " s before the first hit",
                c => FirstContactTicks(c) >= BreatherSeconds * ArenaConfig.TickRate ? null
                    : "first contact after " + FirstContactTicks(c) + " ticks < " + BreatherSeconds * ArenaConfig.TickRate),
            new DesignIntent("readable", "raging enemies stay readable (<= " + MaxReadableSpeed + " lane units per tick)",
                c => RagingSpeed(c) <= MaxReadableSpeed ? null : "raging speed " + RagingSpeed(c) + " > " + MaxReadableSpeed),
        };

        public static List<string> Violations(ArenaConfig c)
        {
            var list = new List<string>();
            foreach (var i in Intents)
            {
                string v = i.Violation(c);
                if (v != null)
                    list.Add(i.Name + ": " + v);
            }
            return list;
        }
    }
}
