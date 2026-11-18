using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using Game.Progress.Contracts;

namespace Game.MathQuiz.Tests
{
    /// <summary>Returns copies of pre-built questions in order (cycles). Every question is a distinct object.</summary>
    internal sealed class ScriptedQuestionGenerator : IQuestionGenerator
    {
        private readonly Question[] _questions;
        private int _index;

        public List<(int Round, int Number)> Requests { get; } = new List<(int, int)>();

        public ScriptedQuestionGenerator(params Question[] questions) => _questions = questions;

        public Question Next(int round, int questionNumber)
        {
            Requests.Add((round, questionNumber));
            var q = _questions[_index++ % _questions.Length];
            return new Question(q.OperandA, q.OperandB, q.Options);
        }

        /// <summary>2 + 3 = 5, options {4, 5, 6}: correct index 1.</summary>
        public static Question TwoPlusThree() => new Question(2, 3, new[] { 4, 5, 6 });
    }

    internal sealed class FakeBestScoreService : IBestScoreService
    {
        public int BestScore { get; set; }
        public List<int> Submitted { get; } = new List<int>();

        public bool SubmitScore(int score)
        {
            Submitted.Add(score);
            if (score <= BestScore)
                return false;
            BestScore = score;
            return true;
        }
    }

    internal sealed class FakeQuizView : IQuizView
    {
        public event Action<int> OptionSelected;
        public event Action NextRequested;
        public event Action RestartRequested;

        public QuestionViewModel LastQuestion;
        public TimerViewModel LastTimer;
        public ArenaViewModel LastArena;
        public AnswerFeedbackViewModel LastFeedback;
        public RoundSummaryViewModel LastSummary;
        public int QuestionShownCount, FeedbackShownCount, ArenaShownCount;
        public List<int> AllZapped { get; } = new List<int>();
        public List<int> AllHitBase { get; } = new List<int>();

        public void ShowQuestion(QuestionViewModel model) { LastQuestion = model; QuestionShownCount++; }
        public void ShowTimer(TimerViewModel model) => LastTimer = model;
        public void ShowAnswerFeedback(AnswerFeedbackViewModel model) { LastFeedback = model; FeedbackShownCount++; }
        public void ShowRoundSummary(RoundSummaryViewModel model) => LastSummary = model;

        public void ShowArena(ArenaViewModel model)
        {
            LastArena = model;
            ArenaShownCount++;
            AllZapped.AddRange(model.Zapped);
            AllHitBase.AddRange(model.HitBase);
        }

        public void ClickOption(int index) => OptionSelected?.Invoke(index);
        public void ClickNext() => NextRequested?.Invoke();
        public void ClickRestart() => RestartRequested?.Invoke();

        public int SubscriberCount => Count(OptionSelected) + Count(NextRequested) + Count(RestartRequested);
        public bool HasSubscribers => SubscriberCount > 0;

        private static int Count(Delegate d) => d == null ? 0 : d.GetInvocationList().Length;
    }

    internal sealed class FakeClock : IGameClock
    {
        public long NowMicroseconds { get; set; }
    }

    /// <summary>
    /// Deterministic frames: nothing happens until the test calls <see cref="Frame"/>, which advances the fake
    /// clock and releases the presenter loop (UniTask continues synchronously, so the frame is fully processed).
    /// </summary>
    internal sealed class ManualFrames : IFrameSource
    {
        private readonly FakeClock _clock;
        private readonly List<(UniTaskCompletionSource Source, CancellationTokenRegistration Registration)> _pending =
            new List<(UniTaskCompletionSource, CancellationTokenRegistration)>();

        public bool IgnoreCancellation { get; set; }
        public int PendingCount => _pending.Count;

        public ManualFrames(FakeClock clock) => _clock = clock;

        public UniTask NextFrame(CancellationToken token)
        {
            if (token.IsCancellationRequested && !IgnoreCancellation)
                return UniTask.FromCanceled(token);
            var source = new UniTaskCompletionSource();
            var entry = (source, default(CancellationTokenRegistration));
            if (!IgnoreCancellation)
                entry.Item2 = token.Register(() =>
                {
                    _pending.RemoveAll(p => p.Source == source);
                    source.TrySetCanceled(token);
                });
            _pending.Add(entry);
            return source.Task;
        }

        /// <summary>One rendered frame that took <paramref name="microseconds"/> of game time.</summary>
        public void Frame(long microseconds)
        {
            _clock.NowMicroseconds += microseconds;
            var batch = _pending.ToArray();
            _pending.Clear();
            foreach (var p in batch)
            {
                p.Registration.Dispose();
                p.Source.TrySetResult();
            }
        }
    }

    internal sealed class RecordingLogger : IGameLogger
    {
        public List<string> Errors { get; } = new List<string>();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) => Errors.Add(message);
    }

    internal static class TestConfigs
    {
        public static QuizConfig Questions(int count, int seconds = 10) => QuizConfig.Create(1, 10, count, 3, 3, seconds).Value;

        /// <summary>An arena where nothing spawns for a long time: isolates quiz flow from slimes.</summary>
        public static ArenaConfig Calm(int feedbackTicks = 30) => ArenaConfig.Create(3, 100000, 100000, 1, 1, 0, 40, 175, 0, 12, feedbackTicks).Value;
    }

    /// <summary>
    /// Real battle + real presenter on fakes. One <see cref="Ticks"/> = frames of exactly one tick (50 ms).
    /// </summary>
    internal sealed class QuizHarness : IDisposable
    {
        public const int Correct = 1; // 2 + 3, options {4, 5, 6}
        public const int Wrong = 2;
        public const long TickMicroseconds = 1_000_000 / ArenaConfig.TickRate;

        public FakeQuizView View { get; } = new FakeQuizView();
        public FakeBestScoreService BestScore { get; }
        public FakeClock Clock { get; } = new FakeClock();
        public ManualFrames Frames { get; }
        public RecordingLogger Logger { get; } = new RecordingLogger();
        public Lifetime App { get; }
        public QuizBattle Battle { get; }
        public QuizSession Session => Battle.Session;
        public QuizPresenter Presenter { get; }

        public QuizHarness(int questions = 10, int seconds = 10, int existingBest = 0, ArenaConfig arena = null, ulong seed = 7, bool scripted = true)
        {
            Frames = new ManualFrames(Clock);
            BestScore = new FakeBestScoreService { BestScore = existingBest };
            App = new Lifetime("test-app", Logger);
            var quiz = TestConfigs.Questions(questions, seconds);
            Battle = scripted
                ? new QuizBattle(quiz, arena ?? TestConfigs.Calm(), new RandomStreams(seed), BestScore,
                    new ScriptedQuestionGenerator(ScriptedQuestionGenerator.TwoPlusThree()), Logger)
                : new QuizBattle(quiz, arena ?? ArenaConfig.Default, new RandomStreams(seed), BestScore, Logger);
            Presenter = App.Own(new QuizPresenter(Battle, View, Clock, Frames, App));
        }

        /// <summary>Starts the round and runs the first tick (question visible).</summary>
        public QuizHarness Begin()
        {
            Presenter.Start();
            Frames.Frame(0);              // first frame only initialises the scheduler
            Ticks(1);
            return this;
        }

        public void Ticks(int count)
        {
            for (int i = 0; i < count; i++)
                Frames.Frame(TickMicroseconds);
        }

        public void Seconds(int seconds) => Ticks(seconds * ArenaConfig.TickRate);

        public void Dispose() => App.Dispose();
    }
}
