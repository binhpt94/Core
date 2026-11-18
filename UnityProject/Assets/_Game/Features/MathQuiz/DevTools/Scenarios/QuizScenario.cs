using System;
using System.Collections.Generic;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using Game.Progress.Contracts;

namespace Game.MathQuiz.DevTools
{
    /// <summary>
    /// A named game state reached the honest way: seed + configs + COMMANDS run through the real simulation.
    /// That makes a scenario three things at once:
    ///   - a cheat ("show me the last-life state") that can never produce a state a player can't reach;
    ///   - a view fixture (the real presenter renders it, see <see cref="QuizScenarioRender"/>);
    ///   - a replay: <see cref="ToReplay"/> gives the .replay.txt, and <see cref="FromReplay"/> turns a bug
    ///     report into a scenario you can open in the preview window.
    /// No field is ever set directly. A state that needs it (e.g. "level 50") must come from a validated save.
    /// </summary>
    internal sealed class QuizScenario
    {
        private readonly Func<IBestScoreService, QuizBattle> _run;

        public string Name { get; }
        public string Description { get; }

        private QuizScenario(string name, string description, Func<IBestScoreService, QuizBattle> run)
        {
            Name = name;
            Description = description;
            _run = run;
        }

        public static QuizScenario Script(string name, string description, Action<QuizScenarioDriver> script,
            ulong seed = 1, QuizConfig quiz = null, ArenaConfig arena = null) =>
            new QuizScenario(name, description, best =>
            {
                var battle = new QuizBattle(quiz ?? QuizConfig.Default, arena ?? ArenaConfig.Default, new RandomStreams(seed), best);
                script(new QuizScenarioDriver(battle));
                return battle;
            });

        /// <summary>Any replay (bug report, golden, last-error dump) as a scenario: runs and verifies it to its end tick.</summary>
        public static QuizScenario FromReplay(string name, string replayText) =>
            new QuizScenario(name, "replay", best =>
            {
                var result = ReplayRunner.Run(ReplayLog.Parse(replayText), meta => QuizBattleReplay.Create(meta, best));
                if (!result.Matches)
                    throw new InvalidOperationException("scenario '" + name + "': " + result);
                return (QuizBattle)result.Simulation;
            });

        public QuizBattle Run(IBestScoreService bestScore = null) => _run(bestScore ?? new InMemoryBestScore());

        /// <summary>The scenario as a replay file (attach to a bug, drop into Tests/Replays, share with QA).</summary>
        public string ToReplay() => Run().ExportReplay();

        public override string ToString() => Name;
    }

    /// <summary>Typed steps for scenario scripts. Every step is a command or the passing of ticks.</summary>
    internal sealed class QuizScenarioDriver
    {
        public const int DefaultMaxTicks = ArenaConfig.TickRate * 600;

        public QuizBattle Battle { get; }

        public QuizScenarioDriver(QuizBattle battle) => Battle = battle;

        public QuizScenarioDriver Start() => Do(QuizCommand.Start());

        public QuizScenarioDriver Next() => Do(QuizCommand.Next());

        public QuizScenarioDriver AnswerCorrect() => Do(QuizCommand.Answer(Current().CorrectIndex));

        public QuizScenarioDriver AnswerWrong()
        {
            var q = Current();
            return Do(QuizCommand.Answer((q.CorrectIndex + 1) % q.Options.Count));
        }

        public QuizScenarioDriver Wait(int ticks)
        {
            for (int i = 0; i < ticks; i++)
                Battle.Step();
            return this;
        }

        public QuizScenarioDriver WaitSeconds(int seconds) => Wait(seconds * ArenaConfig.TickRate);

        /// <summary>Steps until the condition holds; throws if it doesn't within maxTicks (a broken scenario must be loud).</summary>
        public QuizScenarioDriver Until(Func<QuizBattle, bool> condition, string what, int maxTicks = DefaultMaxTicks)
        {
            for (int i = 0; i < maxTicks && !condition(Battle); i++)
                Battle.Step();
            if (!condition(Battle))
                throw new InvalidOperationException("scenario step not reached in " + maxTicks + " ticks: " + what);
            return this;
        }

        /// <summary>Answers every question correctly (and moves on) until the round is over.</summary>
        public QuizScenarioDriver PlayPerfectly()
        {
            for (int guard = 0; guard < DefaultMaxTicks && Battle.Session.State != QuizState.Finished; guard++)
            {
                if (Battle.Session.State == QuizState.AwaitingAnswer)
                    AnswerCorrect();
                else if (Battle.Session.State == QuizState.Answered)
                    Next();
                else
                    Wait(1);
            }
            return this;
        }

        private QuizScenarioDriver Do(QuizCommand command)
        {
            Battle.Enqueue(command);
            Battle.Step(); // commands apply at the start of the next tick
            return this;
        }

        private Question Current() =>
            Battle.Session.CurrentQuestion ?? throw new InvalidOperationException("no current question (call Start first)");
    }

    internal sealed class InMemoryBestScore : IBestScoreService
    {
        public int BestScore { get; private set; }

        public bool SubmitScore(int score)
        {
            if (score <= BestScore)
                return false;
            BestScore = score;
            return true;
        }
    }

    /// <summary>Renders a battle's current state into a view with the REAL presenter (no loop, no clock).</summary>
    internal static class QuizScenarioRender
    {
        public static void Render(QuizBattle battle, IQuizView view)
        {
            using (var lifetime = new Lifetime("scenario-preview", NullGameLogger.Instance))
            using (var presenter = new QuizPresenter(battle, view, new StoppedClock(), new NoFrames(), lifetime))
                presenter.Rebind(view); // renders everything from state, like a UI reload
        }

        private sealed class StoppedClock : IGameClock
        {
            public long NowMicroseconds => 0;
        }

        private sealed class NoFrames : IFrameSource
        {
            public Cysharp.Threading.Tasks.UniTask NextFrame(System.Threading.CancellationToken token) =>
                throw new NotSupportedException("a preview never starts the simulation loop");
        }
    }
}
