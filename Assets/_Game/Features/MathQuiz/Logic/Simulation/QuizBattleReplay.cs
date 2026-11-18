using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.Progress.Contracts;

namespace Game.MathQuiz.Simulation
{
    /// <summary>Rebuilds a <see cref="QuizBattle"/> from replay meta (refusing other versions instead of misreading them).</summary>
    internal static class QuizBattleReplay
    {
        public static QuizBattle Create(IReadOnlyDictionary<string, string> meta, IBestScoreService bestScore)
        {
            Require(meta, "sim", QuizBattle.SimVersion.ToString(CultureInfo.InvariantCulture));
            Require(meta, "rng", RandomStreams.Version.Replace(' ', '_'));
            Require(meta, "tickRate", ArenaConfig.TickRate.ToString(CultureInfo.InvariantCulture));

            var quiz = QuizConfig.FromCanonical(Get(meta, "quiz"));
            var arena = ArenaConfig.FromCanonical(Get(meta, "arena"));
            if (!quiz.IsSuccess) throw new FormatException(quiz.Error.ToString());
            if (!arena.IsSuccess) throw new FormatException(arena.Error.ToString());
            ulong seed = ulong.Parse(Get(meta, "seed"), CultureInfo.InvariantCulture);

            return new QuizBattle(quiz.Value, arena.Value, new RandomStreams(seed), bestScore);
        }

        public static ReplayResult Verify(string replayText, IBestScoreService bestScore) =>
            ReplayRunner.Run(ReplayLog.Parse(replayText), meta => Create(meta, bestScore));

        private static string Get(IReadOnlyDictionary<string, string> meta, string key) =>
            meta.TryGetValue(key, out var v) ? v : throw new FormatException("replay meta missing '" + key + "'");

        private static void Require(IReadOnlyDictionary<string, string> meta, string key, string expected)
        {
            string actual = Get(meta, key);
            if (actual != expected)
                throw new NotSupportedException("replay " + key + "=" + actual + " but this build is " + expected + " (replays are version-locked)");
        }
    }
}
