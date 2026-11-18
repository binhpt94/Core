using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// Countdown = a deadline TICK inside the simulation (no delay chain, no drift). 10 s = 200 ticks exactly,
    /// at any frame rate (see DeterminismTests for 30/60/144 fps).
    /// </summary>
    public sealed class QuizTimerTests
    {
        private const int Correct = QuizHarness.Correct;
        private const int Seconds = 10;
        private const int Rate = ArenaConfig.TickRate;

        private QuizHarness _h;

        [SetUp]
        public void SetUp() => _h = new QuizHarness(questions: 3, seconds: Seconds).Begin();

        [TearDown]
        public void TearDown()
        {
            Assert.That(_h.Logger.Errors, Is.Empty);
            _h.Dispose();
        }

        [Test]
        public void Start_ShowsFullTimer()
        {
            Assert.That(_h.View.LastTimer.Visible, Is.True);
            Assert.That(_h.View.LastTimer.Text, Is.EqualTo(QuizTexts.Timer(Seconds)));
            Assert.That(_h.View.LastTimer.Fraction, Is.EqualTo(1f));
            Assert.That(_h.Battle.DeadlineTick, Is.EqualTo(1 + Seconds * Rate));
        }

        [Test]
        public void OneSecond_CountsDownOne()
        {
            _h.Seconds(1);

            Assert.That(_h.View.LastTimer.Text, Is.EqualTo(QuizTexts.Timer(Seconds - 1)));
            Assert.That(_h.View.LastTimer.Fraction, Is.EqualTo(0.9f).Within(0.0001f));
        }

        [Test]
        [Covers("R-TIMER-URGENT")]
        public void LastSeconds_AreUrgent()
        {
            _h.Seconds(Seconds - QuizPresenter.UrgentSeconds);

            Assert.That(_h.View.LastTimer.Text, Is.EqualTo(QuizTexts.Timer(QuizPresenter.UrgentSeconds)));
            Assert.That(_h.View.LastTimer.IsUrgent, Is.True);
        }

        [Test]
        [Covers("R-TIMER-DEADLINE")]
        public void TimesOut_ExactlyAtTheDeadlineTick()
        {
            _h.Ticks(Seconds * Rate - 1);
            Assert.That(_h.Session.State, Is.EqualTo(QuizState.AwaitingAnswer), "one tick before the deadline");

            _h.Ticks(1);
            Assert.That(_h.Session.State, Is.EqualTo(QuizState.Answered));
            Assert.That(_h.View.LastFeedback.SelectedIndex, Is.EqualTo(AnswerOutcome.NoSelection));
            Assert.That(_h.View.LastFeedback.FeedbackText, Is.EqualTo(QuizTexts.TimeUp(5)));
            Assert.That(_h.View.LastTimer.Visible, Is.False);
            Assert.That(_h.Session.Score, Is.EqualTo(0));
        }

        [Test]
        [Covers("R-TIMER-STOP")]
        public void Answer_StopsTheCountdown()
        {
            _h.Seconds(4);
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            Assert.That(_h.Battle.DeadlineTick, Is.EqualTo(long.MaxValue));
            Assert.That(_h.View.LastTimer.Visible, Is.False);
        }

        [Test]
        [Covers("R-TIMER-STOP")]
        public void Tap_AfterTimeout_IsIgnored()
        {
            _h.Seconds(Seconds);
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            Assert.That(_h.View.FeedbackShownCount, Is.EqualTo(1));
            Assert.That(_h.Session.Score, Is.EqualTo(0));
        }

        [Test]
        public void Feedback_AdvancesAutomatically_AfterFeedbackTicks()
        {
            _h.View.ClickOption(Correct);
            _h.Ticks(1);

            _h.Ticks(TestConfigs.Calm().FeedbackTicks - 1);
            Assert.That(_h.Session.QuestionNumber, Is.EqualTo(1));
            _h.Ticks(1);
            Assert.That(_h.Session.QuestionNumber, Is.EqualTo(2));
            Assert.That(_h.View.LastTimer.Text, Is.EqualTo(QuizTexts.Timer(Seconds)), "new question, full timer");
        }

        [Test]
        public void NoTimerConfigured_TimerHiddenAndNeverTimesOut()
        {
            _h.Dispose();
            _h = new QuizHarness(questions: 3, seconds: 0).Begin();

            _h.Seconds(60);

            Assert.That(_h.View.LastTimer.Visible, Is.False);
            Assert.That(_h.Session.State, Is.EqualTo(QuizState.AwaitingAnswer));
        }
    }
}
