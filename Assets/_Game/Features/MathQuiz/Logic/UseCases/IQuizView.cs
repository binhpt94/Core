using System;
using System.Collections.Generic;

namespace Game.MathQuiz.UseCases
{
    /// <summary>
    /// Passive view: only displays what it is told and forwards user input as events.
    /// Implemented by QuizView (MonoBehaviour) and by FakeQuizView in tests.
    /// All texts arrive pre-formatted, so formatting is unit-testable.
    /// </summary>
    internal interface IQuizView
    {
        event Action<int> OptionSelected;
        event Action NextRequested;
        event Action RestartRequested;

        void ShowQuestion(QuestionViewModel model);
        void ShowTimer(TimerViewModel model);
        void ShowArena(ArenaViewModel model);
        void ShowAnswerFeedback(AnswerFeedbackViewModel model);
        void ShowRoundSummary(RoundSummaryViewModel model);
    }

    internal sealed class QuestionViewModel
    {
        public string QuestionText;
        public IReadOnlyList<string> OptionTexts;
        public string ProgressText;
        public string ScoreText;
    }

    internal sealed class TimerViewModel
    {
        public bool Visible;
        public string Text;

        /// <summary>Remaining time as 0..1 (1 = full).</summary>
        public float Fraction;

        /// <summary>True in the last seconds: the view may highlight it.</summary>
        public bool IsUrgent;
    }

    /// <summary>Arena snapshot for the view. Positions are 0..1 along the lane (0 = base); the view may smooth them.</summary>
    internal sealed class ArenaViewModel
    {
        public string LivesText;
        public int Lives;
        public bool Raging;
        public IReadOnlyList<EnemyViewModel> Enemies;
        /// <summary>Ids zapped / reaching the base since the previous render (for one-shot effects).</summary>
        public IReadOnlyList<int> Zapped;
        public IReadOnlyList<int> HitBase;
    }

    internal readonly struct EnemyViewModel
    {
        public int Id { get; }
        public float Position { get; }

        public EnemyViewModel(int id, float position)
        {
            Id = id;
            Position = position;
        }
    }

    internal sealed class AnswerFeedbackViewModel
    {
        public int SelectedIndex;
        public int CorrectIndex;
        public bool IsCorrect;
        public string FeedbackText;
        public string ScoreText;
        public string NextButtonText;
    }

    internal sealed class RoundSummaryViewModel
    {
        public string TitleText;
        public string DetailText;
        public string RestartButtonText;
    }
}
