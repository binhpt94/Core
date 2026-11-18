using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;

namespace Game.MathQuiz.Domain
{
    /// <summary>
    /// Rules of the "Quiz Defense" arena: slimes walk from the right edge of a lane towards the base at x = 0.
    /// Integer units only (positions in 1/1000 of the lane, speeds per tick, durations in ticks), so the
    /// simulation is bit-identical on every runtime. Immutable and validated, like <see cref="QuizConfig"/>.
    /// </summary>
    internal sealed class ArenaConfig
    {
        /// <summary>Simulation ticks per second. Part of the replay format: changing it changes every replay.</summary>
        public const int TickRate = 20;

        public const int LaneLength = 1000;

        public int BaseLives { get; }
        public int SpawnMinTicks { get; }
        public int SpawnMaxTicks { get; }
        public int SpeedMin { get; }
        public int SpeedMax { get; }
        /// <summary>Chance (percent, per enemy, per tick) that a slime hops in place instead of moving.</summary>
        public int HopPercent { get; }
        /// <summary>Wrong answer / time-out: all slimes rage for this many ticks...</summary>
        public int RageTicks { get; }
        /// <summary>...moving at this percentage of their speed...</summary>
        public int RagePercent { get; }
        /// <summary>...and this many extra slimes spawn.</summary>
        public int PenaltySpawns { get; }
        public int MaxEnemies { get; }
        /// <summary>After an answer the next question comes automatically after this many ticks (or on "Next").</summary>
        public int FeedbackTicks { get; }

        public static ArenaConfig Default { get; } = new ArenaConfig(3, 40, 80, 4, 8, 25, 40, 175, 1, 12, 30);

        private ArenaConfig(int lives, int spawnMin, int spawnMax, int speedMin, int speedMax, int hop, int rageTicks, int ragePercent, int penalty, int maxEnemies, int feedback)
        {
            BaseLives = lives;
            SpawnMinTicks = spawnMin;
            SpawnMaxTicks = spawnMax;
            SpeedMin = speedMin;
            SpeedMax = speedMax;
            HopPercent = hop;
            RageTicks = rageTicks;
            RagePercent = ragePercent;
            PenaltySpawns = penalty;
            MaxEnemies = maxEnemies;
            FeedbackTicks = feedback;
        }

        public static Result<ArenaConfig> Create(int lives, int spawnMin, int spawnMax, int speedMin, int speedMax, int hop = 25,
            int rageTicks = 40, int ragePercent = 175, int penalty = 1, int maxEnemies = 12, int feedback = 30)
        {
            if (lives < 1) return Fail("arena.lives", "BaseLives must be >= 1.");
            if (spawnMin < 1 || spawnMax < spawnMin) return Fail("arena.spawn", "need 1 <= SpawnMinTicks <= SpawnMaxTicks.");
            if (speedMin < 1 || speedMax < speedMin || speedMax > LaneLength) return Fail("arena.speed", "need 1 <= SpeedMin <= SpeedMax <= LaneLength.");
            if (hop < 0 || hop > 100) return Fail("arena.hop", "HopPercent must be 0..100.");
            if (rageTicks < 0 || ragePercent < 100 || ragePercent > 1000) return Fail("arena.rage", "RageTicks >= 0 and RagePercent 100..1000.");
            if (penalty < 0 || maxEnemies < 1) return Fail("arena.enemies", "PenaltySpawns >= 0 and MaxEnemies >= 1.");
            if (feedback < 0) return Fail("arena.feedback", "FeedbackTicks must be >= 0.");
            return Result<ArenaConfig>.Ok(new ArenaConfig(lives, spawnMin, spawnMax, speedMin, speedMax, hop, rageTicks, ragePercent, penalty, maxEnemies, feedback));
        }

        private static Result<ArenaConfig> Fail(string code, string message) => Result<ArenaConfig>.Fail(code, message);

        /// <summary>Same rules, another enemy archetype (content swap). The canonical/replay format is unchanged.</summary>
        public Result<ArenaConfig> WithArchetype(EnemyArchetype a) =>
            Create(BaseLives, SpawnMinTicks, SpawnMaxTicks, a.SpeedMin, a.SpeedMax, a.HopPercent, RageTicks, a.RagePercent, PenaltySpawns, MaxEnemies, FeedbackTicks);

        /// <summary>Canonical text for replays: "lives:3,spawn:40-80,...". Parse with <see cref="FromCanonical"/>.</summary>
        public string Canonical() =>
            "lives:" + BaseLives + ",spawn:" + SpawnMinTicks + "-" + SpawnMaxTicks + ",speed:" + SpeedMin + "-" + SpeedMax +
            ",hop:" + HopPercent + ",rage:" + RageTicks + "-" + RagePercent + ",penalty:" + PenaltySpawns + ",max:" + MaxEnemies + ",feedback:" + FeedbackTicks;

        public static Result<ArenaConfig> FromCanonical(string text)
        {
            try
            {
                var kv = text.Split(',').Select(p => p.Split(':')).ToDictionary(p => p[0], p => p[1]);
                int[] Pair(string k) => kv[k].Split('-').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                int One(string k) => int.Parse(kv[k], CultureInfo.InvariantCulture);
                var spawn = Pair("spawn");
                var speed = Pair("speed");
                var rage = Pair("rage");
                return Create(One("lives"), spawn[0], spawn[1], speed[0], speed[1], One("hop"), rage[0], rage[1], One("penalty"), One("max"), One("feedback"));
            }
            catch (Exception e) when (e is FormatException || e is KeyNotFoundException || e is IndexOutOfRangeException)
            {
                return Fail("arena.canonical", "bad arena config '" + text + "'");
            }
        }
    }
}
