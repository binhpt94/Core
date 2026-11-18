using System.Collections.Generic;
using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;

namespace Game.MathQuiz.DevTools
{
    /// <summary>
    /// The scenario catalog: the states QA, designers and agents need to look at, each reached by commands.
    /// Add one here instead of writing a "CheatWin()" method. Each is verified by QuizScenarioTests (dotnet).
    /// </summary>
    internal static class QuizScenarios
    {
        public static IReadOnlyList<QuizScenario> All() => new[]
        {
            QuizScenario.Script("first-question", "round just started",
                s => s.Start()),

            QuizScenario.Script("answered-correct", "a slime was just zapped",
                s => s.Start().WaitSeconds(2).AnswerCorrect()),

            QuizScenario.Script("answered-wrong", "wrong answer: slimes raging, penalty spawn",
                s => s.Start().WaitSeconds(2).AnswerWrong()),

            QuizScenario.Script("timer-urgent", "last seconds of a question",
                s => s.Start().Until(b => b.SecondsLeft == QuizPresenter.UrgentSeconds - 1, "urgent timer")),

            QuizScenario.Script("last-life", "AFK until one life is left",
                s => s.Start().Until(b => b.Arena.Lives == 1 && b.IsRunning, "one life left")),

            QuizScenario.Script("defeat", "AFK until the base falls",
                s => s.Start().Until(b => b.LastSummary.HasValue && b.LastSummary.Value.Defeated, "defeat")),

            QuizScenario.Script("perfect-round", "every answer correct: summary with a new best",
                s => s.Start().PlayPerfectly(),
                arena: ArenaConfig.Create(3, 400, 400, 1, 1, 0, 40, 175, 0, 12, 30).Value), // slow slimes: nobody reaches the base
        };

        public static QuizScenario Named(string name)
        {
            foreach (var s in All())
                if (s.Name == name)
                    return s;
            throw new KeyNotFoundException("no scenario '" + name + "'");
        }
    }
}
