using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// UI flow through the real simulation (no Unity). Clicks become commands applied on the next tick,
    /// so each test runs one tick after an input (h.Ticks(1)).
    /// </summary>
    public sealed class QuizPresenterTests
    {
        private const int Correct = QuizHarness.Correct;
        private const int Wrong = QuizHarness.Wrong;

        private QuizHarness _h;

        private QuizHarness Create(int questions = 10, int existingBest = 0) =>
            _h = new QuizHarness(questions, seconds: 10, existingBest: existingBest).Begin();

        [TearDown]
        public void TearDown()
        {
            if (_h == null)
                return;
            Assert.That(_h.Logger.Errors, Is.Empty, "no task may fail silently");
            _h.Dispose();
            _h = null;
        }

        [Test]
        public void Start_ShowsFormattedQuestion()
        {
            Create();

            Assert.That(_h.View.LastQuestion.QuestionText, Is.EqualTo("2 + 3 = ?"));
            Assert.That(_h.View.LastQuestion.OptionTexts, Is.EqualTo(new[] { "4", "5", "6" }));
            Assert.That(_h.View.LastQuestion.ProgressText, Is.EqualTo(QuizTexts.Progress(1, 10)));
            Assert.That(_h.View.LastQuestion.ScoreText, Is.EqualTo(QuizTexts.Score(0)));
            Assert.That(_h.View.LastArena.LivesText, Is.EqualTo(QuizTexts.Lives(3)));
        }

        [Test]
        public void Click_IsAppliedOnTheNextTick_NotImmediately()
        {
            Create();

            _h.View.ClickOption(Correct);
            Assert.That(_h.View.FeedbackShownCount, Is.EqualTo(0), "input is a command, not a direct state change");

            _h.Ticks(1);
            Assert.That(_h.View.LastFeedback.IsCorrect, Is.True);
        }

        [Test]
        public void ClickCorrect_ShowsPositiveFeedback()
        {
            Create();
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            Assert.That(_h.View.LastFeedback.FeedbackText, Is.EqualTo(QuizTexts.Correct));
            Assert.That(_h.View.LastFeedback.ScoreText, Is.EqualTo(QuizTexts.Score(1)));
            Assert.That(_h.View.LastFeedback.NextButtonText, Is.EqualTo(QuizTexts.Next));
        }

        [Test]
        public void ClickWrong_ShowsCorrectAnswer()
        {
            Create();
            _h.View.ClickOption(Wrong);
            _h.Ticks(1);

            Assert.That(_h.View.LastFeedback.IsCorrect, Is.False);
            Assert.That(_h.View.LastFeedback.SelectedIndex, Is.EqualTo(Wrong));
            Assert.That(_h.View.LastFeedback.CorrectIndex, Is.EqualTo(Correct));
            Assert.That(_h.View.LastFeedback.FeedbackText, Is.EqualTo(QuizTexts.Wrong(5)));
        }

        [Test]
        [Covers("R-QUIZ-SCORE-ONCE")]
        public void DoubleClick_InTheSameTick_ScoresOnce()
        {
            Create();
            _h.View.ClickOption(Correct);
            _h.View.ClickOption(Wrong);
            _h.Ticks(1);

            Assert.That(_h.View.FeedbackShownCount, Is.EqualTo(1));
            Assert.That(_h.View.LastFeedback.IsCorrect, Is.True);
            Assert.That(_h.Session.Score, Is.EqualTo(1));
        }

        [Test]
        public void ClickNext_ShowsNextQuestionImmediately()
        {
            Create();
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            _h.View.ClickNext();
            _h.Ticks(1);

            Assert.That(_h.View.QuestionShownCount, Is.EqualTo(2));
            Assert.That(_h.View.LastQuestion.ProgressText, Is.EqualTo(QuizTexts.Progress(2, 10)));
            Assert.That(_h.View.LastQuestion.ScoreText, Is.EqualTo(QuizTexts.Score(1)));
        }

        [Test]
        public void ClickNext_BeforeAnswering_IsIgnored()
        {
            Create();
            _h.View.ClickNext();
            _h.Ticks(1);

            Assert.That(_h.View.QuestionShownCount, Is.EqualTo(1));
        }

        [Test]
        public void LastQuestion_NextButtonSaysSeeResult()
        {
            Create(questions: 1);
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            Assert.That(_h.View.LastFeedback.NextButtonText, Is.EqualTo(QuizTexts.SeeResult));
        }

        [Test]
        [Covers("R-SUMMARY-BEST")]
        public void FinishRound_NewBest_ShowsNewBestTitle()
        {
            Create(questions: 2);
            for (int i = 0; i < 2; i++)
            {
                _h.View.ClickOption(Correct);
                _h.Ticks(1);
                _h.View.ClickNext();
                _h.Ticks(1);
            }

            Assert.That(_h.View.LastSummary.TitleText, Is.EqualTo(QuizTexts.NewBest));
            Assert.That(_h.View.LastSummary.DetailText, Is.EqualTo(QuizTexts.Summary(2, 2, 2)));
        }

        [Test]
        [Covers("R-SUMMARY-BEST")]
        public void FinishRound_NotBest_ShowsNormalTitle()
        {
            Create(questions: 1, existingBest: 5);
            _h.View.ClickOption(Wrong);
            _h.Ticks(1);
            _h.View.ClickNext();
            _h.Ticks(1);

            Assert.That(_h.View.LastSummary.TitleText, Is.EqualTo(QuizTexts.RoundFinished));
            Assert.That(_h.View.LastSummary.DetailText, Is.EqualTo(QuizTexts.Summary(0, 1, 5)));
        }

        [Test]
        [Covers("R-QUIZ-ROUND")]
        public void ClickRestart_StartsNewRound()
        {
            Create(questions: 1);
            _h.View.ClickOption(Correct);
            _h.Ticks(1);
            _h.View.ClickNext();
            _h.Ticks(1);

            _h.View.ClickRestart();
            _h.Ticks(1);

            Assert.That(_h.Session.Round, Is.EqualTo(2));
            Assert.That(_h.View.LastQuestion.ProgressText, Is.EqualTo(QuizTexts.Progress(1, 1)));
            Assert.That(_h.View.LastQuestion.ScoreText, Is.EqualTo(QuizTexts.Score(0)));
        }

        [Test]
        public void Dispose_UnsubscribesFromView()
        {
            Create();
            _h.Presenter.Dispose();
            Assert.That(_h.View.HasSubscribers, Is.False);
        }
    }
}
