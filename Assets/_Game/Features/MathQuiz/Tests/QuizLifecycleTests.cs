using System;
using System.Runtime.CompilerServices;
using Game.MathQuiz.UseCases;
using NUnit.Framework;

namespace Game.MathQuiz.Tests
{
    /// <summary>Replay/restart, dispose, rebinding a new view, and the single simulation loop.</summary>
    [Category("Lifecycle")]
    public sealed class QuizLifecycleTests
    {
        private const int Correct = QuizHarness.Correct;

        private QuizHarness _h;

        [SetUp]
        public void SetUp() => _h = new QuizHarness(questions: 3, seconds: 10).Begin();

        [TearDown]
        public void TearDown()
        {
            Assert.That(_h.Logger.Errors, Is.Empty, "no task may fail silently");
            _h.Dispose();
        }

        [Test]
        public void Restart_MidCountdown_ResetsTheRoundAtTheNextTick()
        {
            _h.Seconds(7);
            _h.View.ClickRestart();
            _h.Ticks(1);

            Assert.That(_h.Session.Round, Is.EqualTo(2));
            Assert.That(_h.View.LastTimer.Text, Is.EqualTo(QuizTexts.Timer(10)));
            _h.Seconds(9);
            Assert.That(_h.Session.State, Is.EqualTo(QuizState.AwaitingAnswer), "old deadline is gone");
        }

        [Test]
        public void ReplayManyTimes_OneLoop_NoListenerOrTaskLeak()
        {
            for (int i = 0; i < 10; i++)
            {
                _h.Seconds(1);
                _h.View.ClickRestart();
            }
            _h.Ticks(1);

            Assert.That(_h.Session.Round, Is.EqualTo(11));
            Assert.That(_h.View.SubscriberCount, Is.EqualTo(3));
            Assert.That(_h.Presenter.LoopLifetime.ActiveTaskCount, Is.EqualTo(1), "exactly one simulation loop");
            Assert.That(_h.Frames.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void AppLifetimeEnds_LoopStops()
        {
            var loop = _h.Presenter.LoopLifetime;
            _h.App.Dispose();

            Assert.That(loop.IsAlive, Is.False);
            Assert.That(loop.ActiveTaskCount, Is.EqualTo(0));
            Assert.That(_h.Frames.PendingCount, Is.EqualTo(0));
            Assert.That(_h.View.HasSubscribers, Is.False);
        }

        [Test]
        public void FrameSource_IgnoringCancellation_StillStopsTheLoop()
        {
            _h.Frames.IgnoreCancellation = true;
            _h.Ticks(1);
            long tick = _h.Battle.Tick;

            _h.App.Dispose();
            _h.Ticks(5); // the stale frame completes; the loop must re-check its token and exit

            Assert.That(_h.Battle.Tick, Is.EqualTo(tick));
        }

        [Test]
        public void Start_AfterDispose_Throws()
        {
            _h.Presenter.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _h.Presenter.Start());
        }

        [Test]
        public void Rebind_NewViewMidRound_ShowsExactlyTheSameState()
        {
            _h.Seconds(3);
            _h.View.ClickOption(Correct);
            _h.Ticks(1);
            var old = _h.View;

            var fresh = new FakeQuizView();
            _h.Presenter.Rebind(fresh); // e.g. reskin or UI reload: the old view object is gone

            Assert.That(fresh.LastQuestion.QuestionText, Is.EqualTo(old.LastQuestion.QuestionText));
            Assert.That(fresh.LastQuestion.ProgressText, Is.EqualTo(old.LastQuestion.ProgressText));
            Assert.That(fresh.LastFeedback.FeedbackText, Is.EqualTo(old.LastFeedback.FeedbackText));
            Assert.That(fresh.LastTimer.Visible, Is.EqualTo(old.LastTimer.Visible));
            Assert.That(fresh.LastArena.LivesText, Is.EqualTo(old.LastArena.LivesText));
            Assert.That(old.HasSubscribers, Is.False, "old view released");
            Assert.That(fresh.SubscriberCount, Is.EqualTo(3));

            fresh.ClickNext(); // input now comes from the new view
            _h.Ticks(1);
            Assert.That(fresh.LastQuestion.ProgressText, Is.EqualTo(QuizTexts.Progress(2, 3)));
        }

#if !UNITY_5_3_OR_NEWER
        [Test]
        public void ReplacedView_IsGarbageCollected()
        {
            var weak = RebindAndForget();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(weak.IsAlive, Is.False, "something still references the replaced view");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference RebindAndForget()
        {
            var temp = new FakeQuizView();
            _h.Presenter.Rebind(temp);
            _h.Ticks(2);
            _h.Presenter.Rebind(new FakeQuizView());
            return new WeakReference(temp);
        }
#endif
    }
}
