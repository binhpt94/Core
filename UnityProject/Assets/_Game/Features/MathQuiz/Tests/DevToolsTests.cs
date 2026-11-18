using System.IO;
using System.Linq;
using Game.Core.DevTools;
using Game.MathQuiz.DevTools;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// L0 for the dev tools: every scenario reaches its state through commands, deterministically, and is a
    /// valid replay; every view fixture renders on a fake view. A broken cheat fails HERE, in dotnet, not in QA.
    /// </summary>
    [Category("DevTools")]
    public sealed class QuizScenarioTests
    {
        private static QuizBattle Run(string name) => QuizScenarios.Named(name).Run();

        [Test]
        public void EveryScenario_IsDeterministic_AndAValidReplay()
        {
            foreach (var s in QuizScenarios.All())
            {
                string replay = s.ToReplay();
                Assert.That(s.ToReplay(), Is.EqualTo(replay), s.Name + " is not deterministic");
                var verify = QuizBattleReplay.Verify(replay, new FakeBestScoreService());
                Assert.That(verify.Matches, Is.True, s.Name + ": " + verify);
            }
        }

        [Test]
        public void ScenarioNames_AreUnique() =>
            Assert.That(QuizScenarios.All().Select(s => s.Name), Is.Unique);

        [Test]
        public void Scenarios_ReachTheStateTheirNameClaims()
        {
            Assert.That(Run("first-question").Session.State, Is.EqualTo(QuizState.AwaitingAnswer));
            Assert.That(Run("answered-correct").LastOutcome.Value.IsCorrect, Is.True);

            var wrong = Run("answered-wrong");
            Assert.That(wrong.LastOutcome.Value.IsCorrect, Is.False);
            Assert.That(wrong.Arena.IsRaging(wrong.Tick), Is.True);

            Assert.That(Run("timer-urgent").SecondsLeft, Is.EqualTo(QuizPresenter.UrgentSeconds - 1));
            Assert.That(Run("last-life").Arena.Lives, Is.EqualTo(1));
            Assert.That(Run("defeat").LastSummary.Value.Defeated, Is.True);

            var perfect = Run("perfect-round");
            Assert.That(perfect.LastSummary.Value.Score, Is.EqualTo(perfect.Session.TotalQuestions));
            Assert.That(perfect.LastSummary.Value.IsNewBest, Is.True);
        }

        [Test]
        public void AReplay_IsAScenario_BugReportsOpenInThePreviewAsIs()
        {
            string replay = QuizScenarios.Named("answered-wrong").ToReplay();
            var battle = QuizScenario.FromReplay("bug-123", replay).Run();
            Assert.That(battle.StateHash(), Is.EqualTo(Run("answered-wrong").StateHash()));
        }

        [Test]
        public void GoldenReplays_OpenAsScenarios()
        {
            string dir = Path.Combine(Architecture.Rules.SourceText.FindRepoRoot(TestContext.CurrentContext.TestDirectory), "UnityProject", "Assets", "_Game", "Features", "MathQuiz", "Tests", "Replays");
            if (!Directory.Exists(dir))
                Assert.Ignore("golden replays not found from " + TestContext.CurrentContext.TestDirectory);
            foreach (var file in Directory.GetFiles(dir, "*.replay.txt"))
                Assert.DoesNotThrow(() => QuizScenario.FromReplay(Path.GetFileName(file), File.ReadAllText(file)).Run(), file);
        }
    }

    [Category("DevTools")]
    public sealed class QuizViewFixturesTests
    {
        [Test]
        public void HasExactlyOneSmokeFixture_AndUniqueNames()
        {
            var all = QuizViewFixtures.All();
            Assert.That(all.Count(f => f.Has(FixtureTags.Smoke)), Is.EqualTo(1));
            Assert.That(all.Single(f => f.Has(FixtureTags.Smoke)).Name, Is.EqualTo(FixtureTags.Smoke));
            Assert.That(all.Select(f => f.Name), Is.Unique);
            Assert.That(all.Count(f => f.Has(FixtureTags.Scenario)), Is.EqualTo(QuizScenarios.All().Count), "one fixture per scenario");
            Assert.That(all.Single(f => f.Has(FixtureTags.Smoke)).Has(FixtureTags.Scenario), Is.False, "smoke is stable (snapshot)");
        }

        [Test]
        public void EveryFixture_RendersACompleteScreen_OnAFakeView()
        {
            foreach (var fixture in QuizViewFixtures.All())
            {
                var view = new FakeQuizView();
                fixture.Apply(view);
                Assert.That(view.LastQuestion != null || view.LastSummary != null, Is.True, fixture.Name + ": neither a question nor a summary");
                Assert.That(view.LastTimer, Is.Not.Null, fixture.Name + ": no timer");
                Assert.That(view.LastArena, Is.Not.Null, fixture.Name + ": no arena");
                Assert.That(view.HasSubscribers, Is.False, fixture.Name + ": a preview must not keep listening to the view");
            }
        }

        [Test]
        [Covers("R-SUMMARY-DEFEAT")]
        public void ScenarioFixtures_GoThroughTheRealPresenter()
        {
            var view = new FakeQuizView();
            QuizViewFixtures.All().Single(f => f.Name == "scn-defeat").Apply(view);
            Assert.That(view.LastSummary.TitleText, Is.EqualTo(QuizTexts.Defeated));
            Assert.That(view.LastArena.LivesText, Is.EqualTo(QuizTexts.Lives(0)));
        }

        [Test]
        public void EdgeFixtures_FollowTheConfig_SoAWiderRangeIsTestedAutomatically()
        {
            var wide = Domain.QuizConfig.Create(1, 999, 20, 3, 3, 10).Value;
            var view = new FakeQuizView();
            QuizViewFixtures.All(wide).Single(f => f.Name == "edge-longest-question").Apply(view);
            Assert.That(view.LastQuestion.QuestionText, Is.EqualTo(QuizTexts.Question(999, 999)));
            Assert.That(view.LastQuestion.ProgressText, Is.EqualTo(QuizTexts.Progress(20, 20)));
        }
    }
}
