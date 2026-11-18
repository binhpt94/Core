using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    /// <summary>Gameplay rules of "Quiz Defense", tested directly on the simulation (no presenter, no frames).</summary>
    public sealed class BattleTests
    {
        private FakeBestScoreService _best;

        private QuizBattle Battle(ArenaConfig arena = null, int questions = 10, ulong seed = 3)
        {
            _best = new FakeBestScoreService();
            var b = new QuizBattle(TestConfigs.Questions(questions), arena ?? ArenaConfig.Default, new RandomStreams(seed), _best,
                new ScriptedQuestionGenerator(ScriptedQuestionGenerator.TwoPlusThree()));
            b.Enqueue(QuizCommand.Start());
            b.Step();
            return b;
        }

        private static void Steps(QuizBattle b, int n)
        {
            for (int i = 0; i < n; i++)
                b.Step();
        }

        private static void RunUntil(QuizBattle b, System.Func<bool> condition, int max = 5000)
        {
            for (int i = 0; i < max && !condition(); i++)
                b.Step();
            Assert.That(condition(), Is.True, "condition not reached in " + max + " ticks");
        }

        [Test]
        public void Slimes_SpawnAtTheEdge_AndWalkTowardsTheBase()
        {
            var b = Battle();
            RunUntil(b, () => b.Arena.Enemies.Count > 0);
            var slime = b.Arena.Enemies[0];
            int x = slime.X;
            Assert.That(x, Is.LessThanOrEqualTo(ArenaConfig.LaneLength));

            Steps(b, 20);
            Assert.That(slime.X, Is.LessThan(x));
            Assert.That(slime.Speed, Is.InRange(ArenaConfig.Default.SpeedMin, ArenaConfig.Default.SpeedMax));
        }

        [Test]
        [Covers("R-ARENA-ZAP")]
        public void CorrectAnswer_ZapsTheSlimeClosestToTheBase()
        {
            var b = Battle();
            RunUntil(b, () => b.Arena.Enemies.Count >= 2);
            int nearest = b.Arena.Enemies.OrderBy(e => e.X).ThenBy(e => e.Id).First().Id;

            b.Enqueue(QuizCommand.Answer(QuizHarness.Correct));
            b.Step();

            Assert.That(b.Arena.Enemies.Select(e => e.Id), Does.Not.Contain(nearest));
            Assert.That(b.LastEvents.Any(e => e.Kind == ArenaEventKind.Zapped && e.EnemyId == nearest), Is.True);
        }

        [Test]
        [Covers("R-ARENA-RAGE")]
        public void WrongAnswer_EnragesSlimes_AndSpawnsAPenaltySlime()
        {
            var b = Battle();
            int before = b.Arena.Enemies.Count;

            b.Enqueue(QuizCommand.Answer(QuizHarness.Wrong));
            b.Step();

            Assert.That(b.Arena.IsRaging(b.Tick), Is.True);
            Assert.That(b.Arena.Enemies.Count, Is.EqualTo(before + ArenaConfig.Default.PenaltySpawns));
            Assert.That(b.Arena.IsRaging(b.Tick + ArenaConfig.Default.RageTicks), Is.False, "rage is time-boxed in ticks");
        }

        [Test]
        [Covers("R-ARENA-RAGE")]
        public void TimeOut_IsPunishedLikeAWrongAnswer()
        {
            var b = Battle();
            RunUntil(b, () => b.Session.State == QuizState.Answered);

            Assert.That(b.LastOutcome.Value.IsTimeout, Is.True);
            Assert.That(b.Arena.IsRaging(b.Tick), Is.True);
        }

        [Test]
        [Covers("R-ARENA-DEFEAT")]
        public void SlimesReachingTheBase_CostLives_AndDefeatEndsTheRound()
        {
            var arena = ArenaConfig.Create(2, 5, 5, 200, 200, 0, 0, 100, 0, 12, 30).Value; // fast, no hops
            var b = Battle(arena);

            RunUntil(b, () => b.Session.State == QuizState.Finished);

            Assert.That(b.Arena.Lives, Is.EqualTo(0));
            Assert.That(b.LastSummary.Value.Defeated, Is.True);
            Assert.That(_best.Submitted.Count, Is.EqualTo(1), "best score submitted once, also on defeat");
            var frozen = b.Arena.Enemies.Select(e => e.X).ToList();
            Steps(b, 100);
            Assert.That(b.Arena.Enemies.Select(e => e.X), Is.EqualTo(frozen), "the world is frozen after the round");
        }

        [Test]
        public void KeyedRandomness_ZappingOneSlimeDoesNotChangeAnotherSlimesPath()
        {
            // Same seed; in run B the nearest slime is zapped. A correct answer causes no rage, so every OTHER
            // slime must move exactly as in run A: hops are keyed by (round, enemyId, tick), not by call order.
            var a = Battle(seed: 77);
            var b = Battle(seed: 77);
            RunUntil(a, () => a.Arena.Enemies.Count >= 3);
            RunUntil(b, () => b.Arena.Enemies.Count >= 3);
            int survivor = b.Arena.Enemies.OrderBy(e => e.X).ThenBy(e => e.Id).Skip(1).First().Id;

            b.Enqueue(QuizCommand.Answer(QuizHarness.Correct));
            var pathA = new List<int>();
            var pathB = new List<int>();
            for (int i = 0; i < 25; i++)
            {
                a.Step();
                b.Step();
                pathA.Add(a.Arena.Enemies.First(e => e.Id == survivor).X);
                pathB.Add(b.Arena.Enemies.First(e => e.Id == survivor).X);
            }
            Assert.That(pathB, Is.EqualTo(pathA));
        }

        [Test]
        public void SameSeedAndCommands_SameHash_DifferentSeed_DifferentHash()
        {
            string Run(ulong seed)
            {
                var b = Battle(seed: seed);
                Steps(b, 50);
                b.Enqueue(QuizCommand.Answer(QuizHarness.Wrong));
                Steps(b, 200);
                return b.StateHash();
            }

            Assert.That(Run(5), Is.EqualTo(Run(5)));
            Assert.That(Run(6), Is.Not.EqualTo(Run(5)));
        }

        [Test]
        public void HashCheckpoint_IsRecordedEverySecond()
        {
            var b = Battle();
            Steps(b, 99);
            Assert.That(b.Log.Entries.Where(e => e.Hash != null).Select(e => e.Tick), Is.EqualTo(new long[] { 20, 40, 60, 80, 100 }));
        }
    }
}
