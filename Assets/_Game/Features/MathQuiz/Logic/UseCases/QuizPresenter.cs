using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;

namespace Game.MathQuiz.UseCases
{
    /// <summary>
    /// Drives the simulation in real time and renders it. Owns no gameplay state.
    ///   input:  view events → <see cref="QuizCommand"/>s → queued in the battle (applied next tick)
    ///   time:   one loop (owned by a Lifetime): next frame → clock → <see cref="TickScheduler"/> → N × battle.Step()
    ///   output: everything the view shows is derived from the battle's STATE (not from events), so a view can be
    ///           destroyed and replaced at any moment (<see cref="Rebind"/>) and still show exactly the same thing.
    /// Frame rate only changes how often we render, never what happens.
    /// </summary>
    internal sealed class QuizPresenter : IDisposable
    {
        public const int UrgentSeconds = 3;

        private readonly QuizBattle _battle;
        private readonly IGameClock _clock;
        private readonly IFrameSource _frames;
        private readonly Lifetime _parentLifetime;
        private readonly TickScheduler _scheduler = new TickScheduler(ArenaConfig.TickRate);

        private IQuizView _view;
        private Lifetime _loopLifetime;
        private bool _disposed;

        // What the current view already shows (reset by Rebind).
        private int _renderedVersion = -1;
        private Question _renderedQuestion;
        private int _renderedSeconds = -1;
        private bool _renderedTimerVisible;
        private readonly List<int> _zapped = new List<int>();
        private readonly List<int> _hitBase = new List<int>();

        internal Lifetime LoopLifetime => _loopLifetime;
        internal QuizBattle Battle => _battle;

        public QuizPresenter(QuizBattle battle, IQuizView view, IGameClock clock, IFrameSource frames, Lifetime parentLifetime)
        {
            _battle = battle ?? throw new ArgumentNullException(nameof(battle));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
            _parentLifetime = parentLifetime ?? throw new ArgumentNullException(nameof(parentLifetime));
            Attach(view ?? throw new ArgumentNullException(nameof(view)));
        }

        /// <summary>Starts (or restarts) a round. The simulation loop is started once and lives until Dispose.</summary>
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QuizPresenter));

            _battle.Enqueue(QuizCommand.Start());
            if (_loopLifetime == null)
            {
                _loopLifetime = _parentLifetime.CreateChild("quiz-loop");
                _loopLifetime.Run(LoopAsync);
            }
        }

        /// <summary>Replaces the view (reskin, UI reload) and re-renders everything from the current state.</summary>
        public void Rebind(IQuizView view)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));
            Detach();
            Attach(view);
            _renderedVersion = -1;
            _renderedQuestion = null;
            _renderedSeconds = -1;
            _renderedTimerVisible = !_renderedTimerVisible; // force a timer render
            Render();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _loopLifetime?.Dispose();
            Detach();
        }

        // ---------- time ----------

        private async UniTask LoopAsync(CancellationToken token)
        {
            while (true)
            {
                await _frames.NextFrame(token);
                if (token.IsCancellationRequested || _disposed) // guard after every await (frames may ignore the token)
                    return;

                int ticks = _scheduler.Advance(_clock.NowMicroseconds);
                for (int i = 0; i < ticks; i++)
                {
                    _battle.Step();
                    CollectEffects();
                }
                if (ticks > 0)
                    Render();
            }
        }

        // ---------- input ----------

        private void OnOptionSelected(int index) => _battle.Enqueue(QuizCommand.Answer(index));
        private void OnNextRequested() => _battle.Enqueue(QuizCommand.Next());
        private void OnRestartRequested() => Start();

        private void Attach(IQuizView view)
        {
            _view = view;
            _view.OptionSelected += OnOptionSelected;
            _view.NextRequested += OnNextRequested;
            _view.RestartRequested += OnRestartRequested;
        }

        private void Detach()
        {
            if (_view == null)
                return;
            _view.OptionSelected -= OnOptionSelected;
            _view.NextRequested -= OnNextRequested;
            _view.RestartRequested -= OnRestartRequested;
        }

        // ---------- rendering (state → view models) ----------

        private void CollectEffects()
        {
            foreach (var e in _battle.LastEvents)
            {
                if (e.Kind == ArenaEventKind.Zapped) _zapped.Add(e.EnemyId);
                else if (e.Kind == ArenaEventKind.BaseHit) _hitBase.Add(e.EnemyId);
            }
        }

        private void Render()
        {
            var session = _battle.Session;

            if (_renderedVersion != _battle.Version)
            {
                _renderedVersion = _battle.Version;
                var question = session.CurrentQuestion;
                if (question != null && !ReferenceEquals(question, _renderedQuestion))
                    _view.ShowQuestion(QuestionModel(question));
                _renderedQuestion = question;

                if (session.State == QuizState.Answered && _battle.LastOutcome.HasValue)
                    _view.ShowAnswerFeedback(FeedbackModel(_battle.LastOutcome.Value));
                if (session.State == QuizState.Finished && _battle.LastSummary.HasValue)
                    _view.ShowRoundSummary(SummaryModel(_battle.LastSummary.Value));
            }

            bool timerVisible = session.State == QuizState.AwaitingAnswer && _battle.QuizConfig.HasTimer;
            int seconds = _battle.SecondsLeft;
            if (timerVisible != _renderedTimerVisible || seconds != _renderedSeconds)
            {
                _renderedTimerVisible = timerVisible;
                _renderedSeconds = seconds;
                int total = _battle.QuizConfig.SecondsPerQuestion;
                _view.ShowTimer(new TimerViewModel
                {
                    Visible = timerVisible,
                    Text = QuizTexts.Timer(seconds),
                    Fraction = total <= 0 ? 0f : (float)seconds / total,
                    IsUrgent = seconds <= UrgentSeconds
                });
            }

            _view.ShowArena(ArenaModel());
            _zapped.Clear();
            _hitBase.Clear();
        }

        private QuestionViewModel QuestionModel(Question q)
        {
            var options = new string[q.Options.Count];
            for (int i = 0; i < options.Length; i++)
                options[i] = q.Options[i].ToString();
            var s = _battle.Session;
            return new QuestionViewModel
            {
                QuestionText = QuizTexts.Question(q.OperandA, q.OperandB),
                OptionTexts = options,
                ProgressText = QuizTexts.Progress(s.QuestionNumber, s.TotalQuestions),
                ScoreText = QuizTexts.Score(s.Score)
            };
        }

        private AnswerFeedbackViewModel FeedbackModel(AnswerOutcome outcome)
        {
            var s = _battle.Session;
            int correctAnswer = s.CurrentQuestion.CorrectAnswer;
            return new AnswerFeedbackViewModel
            {
                SelectedIndex = outcome.SelectedIndex,
                CorrectIndex = outcome.CorrectIndex,
                IsCorrect = outcome.IsCorrect,
                FeedbackText = outcome.IsCorrect ? QuizTexts.Correct : outcome.IsTimeout ? QuizTexts.TimeUp(correctAnswer) : QuizTexts.Wrong(correctAnswer),
                ScoreText = QuizTexts.Score(outcome.Score),
                NextButtonText = s.QuestionNumber >= s.TotalQuestions ? QuizTexts.SeeResult : QuizTexts.Next
            };
        }

        private static RoundSummaryViewModel SummaryModel(RoundSummary summary) => new RoundSummaryViewModel
        {
            TitleText = summary.Defeated ? QuizTexts.Defeated : summary.IsNewBest ? QuizTexts.NewBest : QuizTexts.RoundFinished,
            DetailText = QuizTexts.Summary(summary.Score, summary.TotalQuestions, summary.BestScore),
            RestartButtonText = QuizTexts.Restart
        };

        private ArenaViewModel ArenaModel()
        {
            var enemies = _battle.Arena.Enemies;
            var list = new EnemyViewModel[enemies.Count];
            for (int i = 0; i < list.Length; i++)
                list[i] = new EnemyViewModel(enemies[i].Id, (float)enemies[i].X / ArenaConfig.LaneLength);
            return new ArenaViewModel
            {
                Lives = _battle.Arena.Lives,
                LivesText = QuizTexts.Lives(_battle.Arena.Lives),
                Raging = _battle.Arena.IsRaging(_battle.Tick),
                Enemies = list,
                Zapped = _zapped.ToArray(),
                HitBase = _hitBase.ToArray()
            };
        }
    }
}
