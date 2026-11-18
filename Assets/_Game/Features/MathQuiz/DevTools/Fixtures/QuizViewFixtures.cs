using System.Collections.Generic;
using System.Linq;
using Game.Core.DevTools;
using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;

namespace Game.MathQuiz.DevTools
{
    /// <summary>
    /// Every state of QuizView the layout gate, the preview window and screenshots look at.
    ///   smoke + edge (hand-made view models): stable, part of the L3 layout snapshot; edge = limits of the shipped
    ///                      config (longest texts, crowded arena);
    ///   scenario fixtures: the real simulation + real presenter (states a player really sees); L2 constraints only,
    ///                      so a gameplay change never forces a layout-baseline update.
    /// Fixtures are typed C#: renaming a view-model field breaks the build, not a JSON file at runtime.
    /// </summary>
    internal static class QuizViewFixtures
    {
        public static IReadOnlyList<ViewFixture<IQuizView>> All(QuizConfig config = null)
        {
            config = config ?? QuizConfig.Default;
            var list = new List<ViewFixture<IQuizView>>
            {
                // smoke = the minimal state every gate renders. Hand-made (not a scenario) so the layout
                // snapshot never changes because of a gameplay change.
                new ViewFixture<IQuizView>(FixtureTags.Smoke, v =>
                {
                    v.ShowQuestion(new QuestionViewModel
                    {
                        QuestionText = QuizTexts.Question(3, 4),
                        OptionTexts = new[] { "6", "7", "8" },
                        ProgressText = QuizTexts.Progress(1, config.QuestionsPerRound),
                        ScoreText = QuizTexts.Score(0)
                    });
                    v.ShowTimer(Timer(config.SecondsPerQuestion, config.SecondsPerQuestion));
                    v.ShowArena(Arena(ArenaConfig.Default.BaseLives, 2, false));
                }, FixtureTags.Smoke),
            };
            list.AddRange(QuizScenarios.All().Select(s => FromScenario("scn-" + s.Name, s)));
            list.AddRange(Edge(config));
            return list;
        }

        public static ViewFixture<IQuizView> FromScenario(string name, QuizScenario scenario, params string[] extraTags) =>
            new ViewFixture<IQuizView>(name, view => QuizScenarioRender.Render(scenario.Run(), view),
                new[] { FixtureTags.Scenario }.Concat(extraTags).ToArray());

        private static IEnumerable<ViewFixture<IQuizView>> Edge(QuizConfig c)
        {
            int maxSum = c.MaxOperand * 2;
            int total = c.QuestionsPerRound;
            var longestOptions = Enumerable.Range(0, c.OptionCount).Select(i => (maxSum + c.MaxDistractorOffset - i).ToString()).ToArray();
            var longestQuestion = new QuestionViewModel
            {
                QuestionText = QuizTexts.Question(c.MaxOperand, c.MaxOperand),
                OptionTexts = longestOptions,
                ProgressText = QuizTexts.Progress(total, total),
                ScoreText = QuizTexts.Score(total)
            };

            yield return new ViewFixture<IQuizView>("edge-longest-question", v =>
            {
                v.ShowQuestion(longestQuestion);
                v.ShowTimer(Timer(c.SecondsPerQuestion, c.SecondsPerQuestion));
                v.ShowArena(Arena(ArenaConfig.Default.BaseLives, 0, false));
            }, FixtureTags.Edge);

            yield return new ViewFixture<IQuizView>("edge-longest-feedback", v =>
            {
                v.ShowQuestion(longestQuestion);
                v.ShowTimer(new TimerViewModel { Visible = false, Text = "" });
                v.ShowArena(Arena(1, ArenaConfig.Default.MaxEnemies, true));
                v.ShowAnswerFeedback(new AnswerFeedbackViewModel
                {
                    SelectedIndex = 0,
                    CorrectIndex = c.OptionCount - 1,
                    IsCorrect = false,
                    FeedbackText = new[] { QuizTexts.Wrong(maxSum), QuizTexts.TimeUp(maxSum) }.OrderByDescending(t => t.Length).First(),
                    ScoreText = QuizTexts.Score(total),
                    NextButtonText = new[] { QuizTexts.Next, QuizTexts.SeeResult }.OrderByDescending(t => t.Length).First()
                });
            }, FixtureTags.Edge);

            yield return new ViewFixture<IQuizView>("edge-longest-summary", v =>
            {
                v.ShowQuestion(longestQuestion);
                v.ShowTimer(new TimerViewModel { Visible = false, Text = "" });
                v.ShowArena(Arena(0, 0, false));
                v.ShowRoundSummary(new RoundSummaryViewModel
                {
                    TitleText = new[] { QuizTexts.Defeated, QuizTexts.NewBest, QuizTexts.RoundFinished }.OrderByDescending(t => t.Length).First(),
                    DetailText = QuizTexts.Summary(total, total, total),
                    RestartButtonText = QuizTexts.Restart
                });
            }, FixtureTags.Edge);
        }

        private static TimerViewModel Timer(int seconds, int total) => new TimerViewModel
        {
            Visible = total > 0,
            Text = QuizTexts.Timer(seconds),
            Fraction = total <= 0 ? 0f : (float)seconds / total,
            IsUrgent = seconds <= QuizPresenter.UrgentSeconds
        };

        /// <summary>count slimes spread evenly along the lane (deterministic, no RNG).</summary>
        private static ArenaViewModel Arena(int lives, int count, bool raging) => new ArenaViewModel
        {
            Lives = lives,
            LivesText = QuizTexts.Lives(lives),
            Raging = raging,
            Enemies = Enumerable.Range(0, count).Select(i => new EnemyViewModel(i + 1, (i + 1f) / (count + 1f))).ToArray(),
            Zapped = new int[0],
            HitBase = new int[0]
        };
    }
}
