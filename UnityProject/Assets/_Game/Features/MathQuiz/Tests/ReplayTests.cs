using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// Reproducibility: frame-rate independence, live play → replay, divergence detection, version lock and the
    /// golden replays in Tests/Replays (run in dotnet AND Unity EditMode = cross-runtime determinism check).
    /// To turn a player's bug report into a regression test: drop its .replay.txt into Tests/Replays.
    /// </summary>
    [Category("Replay")]
    public sealed class ReplayTests
    {
        // ---------- frame-rate independence ----------

        private static string HashAtTick(int fps, long tick, bool jitter = false)
        {
            using (var h = new QuizHarness(questions: 10, seconds: 10, seed: 2024, scripted: false, arena: ArenaConfig.Default))
            {
                h.Presenter.Start();
                h.Frames.Frame(0);
                var rng = new DeterministicRandom(9);
                while (h.Battle.Tick < tick)
                    h.Frames.Frame(jitter ? rng.Next(1_000, 120_000) : 1_000_000 / fps);
                return h.Battle.Log.Entries.First(e => e.Hash != null && e.Tick == tick).Hash;
            }
        }

        [Test]
        [Covers("R-TIMER-FPS")]
        public void SameGameTime_SameState_AtAnyFrameRate()
        {
            string at60 = HashAtTick(60, 600);
            Assert.That(HashAtTick(30, 600), Is.EqualTo(at60));
            Assert.That(HashAtTick(144, 600), Is.EqualTo(at60));
            Assert.That(HashAtTick(0, 600, jitter: true), Is.EqualTo(at60), "random frame times (1..120 ms)");
        }

        // ---------- live → replay ----------

        private static string PlayLive(ulong seed)
        {
            using (var h = new QuizHarness(questions: 6, seconds: 6, seed: seed, scripted: false, arena: ArenaConfig.Default))
            {
                h.Presenter.Start();
                h.Frames.Frame(0);
                var human = new DeterministicRandom(seed + 1); // simulated, irregular human timing at ~60 fps
                for (int frame = 0; frame < 3000 && h.Session.State != QuizState.Finished; frame++)
                {
                    h.Frames.Frame(human.Next(12_000, 22_000));
                    int roll = human.Next(0, 200);
                    if (roll < 2) h.View.ClickOption(human.Next(0, 3));
                    else if (roll == 2) h.View.ClickNext();
                    else if (roll == 3 && frame > 1500) h.View.ClickRestart();
                }
                return h.Battle.ExportReplay();
            }
        }

        [Test]
        [Covers("R-REPLAY-BITS")]
        public void LivePlay_ReplaysBitForBit()
        {
            string replay = PlayLive(31);
            var result = QuizBattleReplay.Verify(replay, new FakeBestScoreService());
            Assert.That(result.Matches, Is.True, result + "\n" + replay);
            Assert.That(replay, Does.Contain("cmd "), "the session had input");
        }

        [Test]
        [Covers("R-REPLAY-DIVERGE")]
        public void TamperedReplay_ReportsTheFirstDivergentTick()
        {
            string replay = PlayLive(32);
            var firstAnswer = ReplayLog.Parse(replay).Entries.First(e => e.Command != null && e.Command.StartsWith("Answer", StringComparison.Ordinal));
            string original = "cmd " + firstAnswer.Tick + " " + firstAnswer.Command;
            long later = firstAnswer.Tick + 1; // the player answered one tick later
            int at = replay.IndexOf(original + "\n", StringComparison.Ordinal);
            string tampered = replay.Substring(0, at) + "cmd " + later + " " + firstAnswer.Command + replay.Substring(at + original.Length);

            var result = QuizBattleReplay.Verify(tampered, new FakeBestScoreService());

            Assert.That(result.Matches, Is.False);
            Assert.That(result.DivergedAtTick, Is.GreaterThanOrEqualTo(later));
            Assert.That(result.DivergedAtTick, Is.LessThanOrEqualTo(later + ArenaConfig.TickRate), "found within one checkpoint");
        }

        [Test]
        public void ReplayOfAnotherSimVersion_IsRefused_NotMisread()
        {
            string replay = PlayLive(33).Replace("sim=" + QuizBattle.SimVersion, "sim=999");
            Assert.Throws<NotSupportedException>(() => QuizBattleReplay.Verify(replay, new FakeBestScoreService()));
        }

        // ---------- golden replays ----------

        private static string ReplayDir()
        {
            string root = Architecture.Rules.SourceText.FindRepoRoot(TestContext.CurrentContext.TestDirectory);
            return Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", "MathQuiz", "Tests", "Replays");
        }

        /// <summary>Deterministic bots that generate the golden replays (only used when GOLDEN_WRITE=1).</summary>
        private static string Bot(string name, ulong seed)
        {
            var battle = new QuizBattle(TestConfigs.Questions(8, 8), ArenaConfig.Default, new RandomStreams(seed), new FakeBestScoreService());
            battle.Enqueue(QuizCommand.Start());
            long askedAt = 0;
            int answered = 0;
            for (int t = 0; t < 4000 && !(battle.Session.State == QuizState.Finished && battle.Tick > 10); t++)
            {
                battle.Step();
                var s = battle.Session;
                if (s.State != QuizState.AwaitingAnswer) { askedAt = battle.Tick; continue; }
                long waited = battle.Tick - askedAt;
                switch (name)
                {
                    case "perfect" when waited == 15:
                        battle.Enqueue(QuizCommand.Answer(s.CurrentQuestion.CorrectIndex));
                        break;
                    case "chaotic" when waited == 10 + answered * 7 % 50:
                        battle.Enqueue(QuizCommand.Answer(answered % 3 == 0 ? (s.CurrentQuestion.CorrectIndex + 1) % 3 : s.CurrentQuestion.CorrectIndex));
                        if (answered % 2 == 1) battle.Enqueue(QuizCommand.Next());
                        answered++;
                        if (answered == 5) battle.Enqueue(QuizCommand.Start()); // restart mid-round
                        break;
                }
            }
            return battle.ExportReplay();
        }

        private static IEnumerable<string> Goldens => new[] { "perfect", "afk", "chaotic" };

        [TestCaseSource(nameof(Goldens))]
        [Covers("R-REPLAY-GOLDEN")]
        public void GoldenReplay_StillMatches(string name)
        {
            string path = Path.Combine(ReplayDir(), name + ".replay.txt");
            if (Environment.GetEnvironmentVariable("GOLDEN_WRITE") == "1") // owners only, when a rule change is intended
                File.WriteAllText(path, Bot(name, 1000UL + (ulong)name.Length));

            Assert.That(File.Exists(path), Is.True, "missing golden replay " + path);
            var result = QuizBattleReplay.Verify(File.ReadAllText(path), new FakeBestScoreService());
            Assert.That(result.Matches, Is.True, name + ": " + result +
                "\nIf the gameplay change is intended, an owner regenerates with GOLDEN_WRITE=1 and reviews the diff.");
        }

        [Test]
        [Covers("R-REPLAY-GOLDEN")]
        public void EveryReplayFileInTheFolder_Matches()
        {
            // Bug reports dropped here become regression tests automatically.
            var files = Directory.GetFiles(ReplayDir(), "*.replay.txt");
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(3));
            foreach (var f in files)
            {
                var r = QuizBattleReplay.Verify(File.ReadAllText(f), new FakeBestScoreService());
                Assert.That(r.Matches, Is.True, Path.GetFileName(f) + ": " + r);
            }
        }

        [Test]
        public void Goldens_CoverWinDefeatAndRestart()
        {
            var perfect = QuizBattleReplay.Verify(File.ReadAllText(Path.Combine(ReplayDir(), "perfect.replay.txt")), new FakeBestScoreService());
            var afk = QuizBattleReplay.Verify(File.ReadAllText(Path.Combine(ReplayDir(), "afk.replay.txt")), new FakeBestScoreService());
            var chaotic = File.ReadAllText(Path.Combine(ReplayDir(), "chaotic.replay.txt"));

            Assert.That(((QuizBattle)perfect.Simulation).LastSummary.Value.Defeated, Is.False);
            Assert.That(((QuizBattle)afk.Simulation).LastSummary.Value.Defeated, Is.True);
            Assert.That(chaotic.Split('\n').Count(l => l.EndsWith(" Start", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(2));
        }
    }
}
