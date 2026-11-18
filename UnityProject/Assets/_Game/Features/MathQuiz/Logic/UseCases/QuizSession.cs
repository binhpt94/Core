using System.Collections.Generic;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.Progress.Contracts;

namespace Game.MathQuiz.UseCases
{
    internal enum QuizState
    {
        NotStarted,
        AwaitingAnswer,
        Answered,
        Finished
    }

    internal readonly struct AnswerOutcome
    {
        /// <summary><see cref="SelectedIndex"/> value when time ran out.</summary>
        public const int NoSelection = -1;

        public int SelectedIndex { get; }
        public int CorrectIndex { get; }
        public bool IsCorrect => SelectedIndex == CorrectIndex;
        public bool IsTimeout => SelectedIndex == NoSelection;
        public int Score { get; }

        public AnswerOutcome(int selectedIndex, int correctIndex, int score)
        {
            SelectedIndex = selectedIndex;
            CorrectIndex = correctIndex;
            Score = score;
        }
    }

    internal readonly struct RoundSummary
    {
        public int Score { get; }
        public int TotalQuestions { get; }
        public int BestScore { get; }
        public bool IsNewBest { get; }
        public bool Defeated { get; }

        public RoundSummary(int score, int totalQuestions, int bestScore, bool isNewBest, bool defeated = false)
        {
            Defeated = defeated;
            Score = score;
            TotalQuestions = totalQuestions;
            BestScore = bestScore;
            IsNewBest = isNewBest;
        }
    }

    /// <summary>
    /// One round of the quiz as an explicit state machine. <see cref="Transitions"/> is the single source of
    /// truth: every move is checked against it, and the state diagram in Docs/Architecture/overview.md is
    /// generated from it. Invalid calls (double tap, wrong state) return a failed Result instead of throwing.
    /// </summary>
    internal sealed class QuizSession
    {
        public const string StartTrigger = "Start";
        public const string AnswerTrigger = "Answer";
        public const string TimeOutTrigger = "TimeOut";
        public const string NextTrigger = "Next";
        public const string FinishTrigger = "Finish";
        public const string DefeatTrigger = "Defeat";

        // One transition per line: parsed by the graph generator (test-gate.sh graph).
        internal static IReadOnlyList<Transition<QuizState>> Transitions { get; } = new[]
        {
            new Transition<QuizState>(QuizState.NotStarted, StartTrigger, QuizState.AwaitingAnswer),
            new Transition<QuizState>(QuizState.AwaitingAnswer, AnswerTrigger, QuizState.Answered),
            new Transition<QuizState>(QuizState.AwaitingAnswer, TimeOutTrigger, QuizState.Answered),
            new Transition<QuizState>(QuizState.Answered, NextTrigger, QuizState.AwaitingAnswer),
            new Transition<QuizState>(QuizState.Answered, FinishTrigger, QuizState.Finished),
            new Transition<QuizState>(QuizState.Finished, StartTrigger, QuizState.AwaitingAnswer),
            new Transition<QuizState>(QuizState.AwaitingAnswer, StartTrigger, QuizState.AwaitingAnswer),
            new Transition<QuizState>(QuizState.Answered, StartTrigger, QuizState.AwaitingAnswer),
            new Transition<QuizState>(QuizState.AwaitingAnswer, DefeatTrigger, QuizState.Finished),
            new Transition<QuizState>(QuizState.Answered, DefeatTrigger, QuizState.Finished),
        };

        public const string ErrorWrongState = "quiz.wrong_state";
        public const string ErrorInvalidOption = "quiz.invalid_option";

        private readonly QuizConfig _config;
        private readonly IQuestionGenerator _generator;
        private readonly IBestScoreService _bestScore;

        public QuizState State { get; private set; } = QuizState.NotStarted;
        public Question CurrentQuestion { get; private set; }

        /// <summary>1-based round counter (incremented by every Start). Key of the question RNG streams.</summary>
        public int Round { get; private set; }

        /// <summary>1-based number of the current question.</summary>
        public int QuestionNumber { get; private set; }

        public int TotalQuestions => _config.QuestionsPerRound;
        public QuizConfig Config => _config;
        public int Score { get; private set; }

        public QuizSession(QuizConfig config, IQuestionGenerator generator, IBestScoreService bestScore)
        {
            _config = config ?? throw new System.ArgumentNullException(nameof(config));
            _generator = generator ?? throw new System.ArgumentNullException(nameof(generator));
            _bestScore = bestScore ?? throw new System.ArgumentNullException(nameof(bestScore));
        }

        /// <summary>Starts (or restarts) a round. Always allowed.</summary>
        public void Start()
        {
            Round++;
            Score = 0;
            QuestionNumber = 0;
            MoveToNextQuestion(StartTrigger);
        }

        public Result<AnswerOutcome> SubmitAnswer(int optionIndex)
        {
            if (!TransitionTable.Allows(Transitions, State, AnswerTrigger))
                return Result<AnswerOutcome>.Fail(ErrorWrongState, "Cannot answer in state " + State + ".");

            if (!CurrentQuestion.IsValidOption(optionIndex))
                return Result<AnswerOutcome>.Fail(ErrorInvalidOption, "Option index out of range: " + optionIndex + ".");

            if (CurrentQuestion.IsCorrect(optionIndex))
                Score++;

            Move(AnswerTrigger, QuizState.Answered);
            return Result<AnswerOutcome>.Ok(new AnswerOutcome(optionIndex, CurrentQuestion.CorrectIndex, Score));
        }

        /// <summary>Time ran out for the current question: counts as wrong.</summary>
        public Result<AnswerOutcome> TimeOut()
        {
            if (!TransitionTable.Allows(Transitions, State, TimeOutTrigger))
                return Result<AnswerOutcome>.Fail(ErrorWrongState, "Cannot time out in state " + State + ".");

            Move(TimeOutTrigger, QuizState.Answered);
            return Result<AnswerOutcome>.Ok(
                new AnswerOutcome(AnswerOutcome.NoSelection, CurrentQuestion.CorrectIndex, Score));
        }

        /// <summary>
        /// Moves to the next question. Returns a summary (HasValue) when the round just finished.
        /// </summary>
        public Result<RoundSummary?> Advance()
        {
            if (!TransitionTable.Allows(Transitions, State, NextTrigger))
                return Result<RoundSummary?>.Fail(ErrorWrongState, "Cannot advance in state " + State + ".");

            if (QuestionNumber >= TotalQuestions)
                return Result<RoundSummary?>.Ok(Finish(FinishTrigger, defeated: false));

            MoveToNextQuestion(NextTrigger);
            return Result<RoundSummary?>.Ok(null);
        }

        /// <summary>The base was destroyed: the round ends now (score so far still counts).</summary>
        public Result<RoundSummary> Defeat()
        {
            if (!TransitionTable.Allows(Transitions, State, DefeatTrigger))
                return Result<RoundSummary>.Fail(ErrorWrongState, "Cannot be defeated in state " + State + ".");
            return Result<RoundSummary>.Ok(Finish(DefeatTrigger, defeated: true));
        }

        private RoundSummary Finish(string trigger, bool defeated)
        {
            Move(trigger, QuizState.Finished);
            CurrentQuestion = null;
            bool isNewBest = _bestScore.SubmitScore(Score);
            return new RoundSummary(Score, TotalQuestions, _bestScore.BestScore, isNewBest, defeated);
        }

        private void Move(string trigger, QuizState to)
        {
            TransitionTable.Ensure(Transitions, State, trigger, to);
            State = to;
        }

        private void MoveToNextQuestion(string trigger)
        {
            CurrentQuestion = _generator.Next(Round, QuestionNumber + 1);
            QuestionNumber++;
            Move(trigger, QuizState.AwaitingAnswer);
        }
    }
}
