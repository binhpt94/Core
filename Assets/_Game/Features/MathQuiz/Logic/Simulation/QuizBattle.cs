using System.Collections.Generic;
using System.Globalization;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;
using Game.Progress.Contracts;

namespace Game.MathQuiz.Simulation
{
    /// <summary>
    /// "Quiz Defense" as a deterministic, fixed-tick simulation (20 ticks/s). It decides WHAT happens;
    /// the presenter/view only decide how it looks. Inputs are exclusively:
    ///   - the session seed (via <see cref="IRandomStreams"/>), the configs and <see cref="SimVersion"/>;
    ///   - commands, applied at the start of the next <see cref="Step"/>, in order.
    /// No wall-clock time, no floats, no Unity, no async: the same inputs give the same state hash on
    /// CoreCLR, Mono and IL2CPP, at any frame rate. Every command and a hash checkpoint every second go
    /// to <see cref="Log"/>, which <see cref="ReplayRunner"/> can re-run.
    ///
    /// Per tick, in this fixed order:
    ///   1. commands  2. time-out  3. auto-next  4. arena (spawn, move, base hits)  5. defeat  6. checkpoint
    /// </summary>
    internal sealed class QuizBattle : IReplayableSimulation
    {
        /// <summary>Bump when gameplay rules change: replays of another version are refused, not misread.</summary>
        public const int SimVersion = 1;

        private readonly QuizSession _session;
        private readonly Arena _arena;
        private readonly QuizConfig _quiz;
        private readonly ArenaConfig _arenaConfig;
        private readonly IGameLogger _logger;
        private readonly List<QuizCommand> _pending = new List<QuizCommand>();
        private readonly List<ArenaEvent> _events = new List<ArenaEvent>();

        public long Tick { get; private set; }
        public QuizSession Session => _session;
        public Arena Arena => _arena;
        public QuizConfig QuizConfig => _quiz;
        public ReplayLog Log { get; } = new ReplayLog();

        /// <summary>Tick at which the current question times out (long.MaxValue if no timer / not asking).</summary>
        public long DeadlineTick { get; private set; } = long.MaxValue;
        public long FeedbackUntilTick { get; private set; } = long.MaxValue;
        public AnswerOutcome? LastOutcome { get; private set; }
        public RoundSummary? LastSummary { get; private set; }

        /// <summary>Changes whenever something the static UI shows changes (question, phase, outcome, score, lives).</summary>
        public int Version { get; private set; }

        /// <summary>Arena events of the last <see cref="Step"/> (for effects only: the state is the truth).</summary>
        public IReadOnlyList<ArenaEvent> LastEvents => _events;

        public QuizBattle(QuizConfig quiz, ArenaConfig arena, IRandomStreams streams, IBestScoreService bestScore, IGameLogger logger = null)
            : this(quiz, arena, streams, bestScore, new QuestionGenerator(quiz, streams), logger)
        {
        }

        /// <summary>Tests may script the questions; everything else stays real.</summary>
        internal QuizBattle(QuizConfig quiz, ArenaConfig arena, IRandomStreams streams, IBestScoreService bestScore, IQuestionGenerator generator, IGameLogger logger = null)
        {
            _quiz = quiz;
            _arenaConfig = arena;
            _logger = logger ?? NullGameLogger.Instance;
            _session = new QuizSession(quiz, generator, bestScore);
            _arena = new Arena(arena, streams);

            Log.Meta["sim"] = SimVersion.ToString(CultureInfo.InvariantCulture);
            Log.Meta["rng"] = RandomStreams.Version.Replace(' ', '_');
            Log.Meta["seed"] = streams.Seed.ToString(CultureInfo.InvariantCulture);
            Log.Meta["tickRate"] = ArenaConfig.TickRate.ToString(CultureInfo.InvariantCulture);
            Log.Meta["quiz"] = quiz.Canonical();
            Log.Meta["arena"] = arena.Canonical();
        }

        public bool IsRunning => _session.State == QuizState.AwaitingAnswer || _session.State == QuizState.Answered;

        public void Enqueue(QuizCommand command) => _pending.Add(command);

        public bool Enqueue(string command)
        {
            if (!QuizCommand.TryParse(command, out var c))
                return false;
            Enqueue(c);
            return true;
        }

        public void Step()
        {
            Tick++;
            _events.Clear();

            for (int i = 0; i < _pending.Count; i++) // a command may enqueue nothing; index loop is order-stable
            {
                Log.RecordCommand(Tick, _pending[i].ToString());
                Apply(_pending[i]);
            }
            _pending.Clear();

            if (IsRunning)
            {
                if (_session.State == QuizState.AwaitingAnswer && Tick >= DeadlineTick)
                    Judge(_session.TimeOut());

                if (_session.State == QuizState.Answered && Tick >= FeedbackUntilTick)
                    AdvanceQuestion();

                if (IsRunning)
                {
                    int lives = _arena.Lives;
                    _arena.Step(_session.Round, Tick, _events);
                    if (_arena.Lives != lives)
                        Version++;
                    if (_arena.Lives == 0)
                        Finish(_session.Defeat());
                }
            }

            if (Tick % ArenaConfig.TickRate == 0)
                Log.RecordHash(Tick, StateHash());
        }

        private void Apply(QuizCommand command)
        {
            switch (command.Kind)
            {
                case QuizCommandKind.Start:
                    _arena.Reset(_session.Round + 1, Tick);
                    _session.Start();
                    LastOutcome = null;
                    LastSummary = null;
                    AskCurrentQuestion();
                    break;
                case QuizCommandKind.Answer:
                    Judge(_session.SubmitAnswer(command.Option));
                    break;
                case QuizCommandKind.Next:
                    if (_session.State == QuizState.Answered)
                        AdvanceQuestion();
                    else
                        _logger.Info("Next ignored in state " + _session.State);
                    break;
            }
        }

        private void Judge(Result<AnswerOutcome> result)
        {
            if (!result.IsSuccess)
            {
                _logger.Info("Answer ignored: " + result.Error); // double tap, tap after time-out...
                return;
            }

            var outcome = result.Value;
            if (outcome.IsCorrect)
                _arena.ZapNearest(_events);
            else
                _arena.Punish(_session.Round, Tick, _events);

            LastOutcome = outcome;
            DeadlineTick = long.MaxValue;
            FeedbackUntilTick = Tick + _arenaConfig.FeedbackTicks;
            Version++;
        }

        private void AdvanceQuestion()
        {
            var result = _session.Advance();
            if (!result.IsSuccess)
                return;
            if (result.Value.HasValue)
                Finish(Result<RoundSummary>.Ok(result.Value.Value));
            else
                AskCurrentQuestion();
        }

        private void AskCurrentQuestion()
        {
            LastOutcome = null;
            FeedbackUntilTick = long.MaxValue;
            DeadlineTick = _quiz.HasTimer ? Tick + (long)_quiz.SecondsPerQuestion * ArenaConfig.TickRate : long.MaxValue;
            Version++;
        }

        private void Finish(Result<RoundSummary> result)
        {
            if (!result.IsSuccess)
                return;
            LastSummary = result.Value;
            DeadlineTick = long.MaxValue;
            FeedbackUntilTick = long.MaxValue;
            Version++;
        }

        /// <summary>Whole seconds left for the current question (0 when not asking).</summary>
        public int SecondsLeft =>
            DeadlineTick == long.MaxValue ? 0 : (int)((DeadlineTick - Tick + ArenaConfig.TickRate - 1) / ArenaConfig.TickRate);

        /// <summary>Canonical hash of everything that influences the future (never view state).</summary>
        public string StateHash()
        {
            var h = new StateHashBuilder()
                .Add(SimVersion).Add(Tick).Add(_session.Round).Add((int)_session.State).Add(_session.QuestionNumber).Add(_session.Score)
                .Add(DeadlineTick).Add(FeedbackUntilTick);
            var q = _session.CurrentQuestion;
            h.Add(q != null);
            if (q != null)
            {
                h.Add(q.OperandA).Add(q.OperandB).Add(q.Options.Count);
                foreach (var o in q.Options)
                    h.Add(o);
            }
            _arena.AddToHash(h);
            return h.Hex;
        }

        /// <summary>Text replay of this session (meta + commands + checkpoints + final hash).</summary>
        public string ExportReplay()
        {
            Log.End(Tick, StateHash());
            return Log.Serialize();
        }
    }
}
