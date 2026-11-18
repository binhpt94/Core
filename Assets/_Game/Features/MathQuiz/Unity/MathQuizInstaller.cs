using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.Simulation;
using Game.MathQuiz.UseCases;
using Game.Progress.Contracts;
using UnityEngine;

namespace Game.MathQuiz.Presentation
{
    /// <summary>Public face of the MathQuiz feature for the composition root. Logic types stay internal.</summary>
    public sealed class MathQuizModule
    {
        private readonly QuizPresenter _presenter;

        public QuizView View { get; }

        /// <summary>Session seed: with the replay commands, it reproduces this session exactly.</summary>
        public ulong Seed => _presenter.Battle.Log.Meta.TryGetValue("seed", out var s) ? ulong.Parse(s) : 0;

        internal MathQuizModule(QuizPresenter presenter, QuizView view)
        {
            _presenter = presenter;
            View = view;
        }

        public void Start() => _presenter.Start();

        /// <summary>Text replay (seed, versions, configs, every command with its tick, hash checkpoints).</summary>
        public string ExportReplay() => _presenter.Battle.ExportReplay();
    }

    /// <summary>
    /// The only place that knows how the MathQuiz feature is assembled. Bootstrap passes shared services
    /// (seeded random streams, game clock, frame source) and the feature's asset entry.
    /// </summary>
    public static class MathQuizInstaller
    {
        public static MathQuizModule Install(
            MathQuizAssets assets,
            Transform uiParent,
            IRandomStreams streams,
            IGameClock clock,
            IFrameSource frames,
            IBestScoreService bestScore,
            Lifetime lifetime,
            IGameLogger logger)
        {
            var quiz = ResolveQuiz(assets, logger);
            var arena = ResolveArena(assets, logger);
            var view = CreateView(assets, uiParent);

            var battle = new QuizBattle(quiz, arena, streams, bestScore, logger);
            battle.Log.Meta["build"] = Application.version.Replace(' ', '_'); // informational only
            var presenter = lifetime.Own(new QuizPresenter(battle, view, clock, frames, lifetime));
            return new MathQuizModule(presenter, view);
        }

        private static QuizConfig ResolveQuiz(MathQuizAssets assets, IGameLogger logger)
        {
            // Explicit == null: never ?. / ?? on UnityEngine.Object (fake null).
            if (assets == null || assets.Config == null)
                return QuizConfig.Default;
            var result = assets.Config.ToConfig();
            if (result.IsSuccess)
                return result.Value;
            logger.Error("Invalid " + assets.Config.name + " (" + result.Error + "). Falling back to defaults.");
            return QuizConfig.Default;
        }

        private static ArenaConfig ResolveArena(MathQuizAssets assets, IGameLogger logger)
        {
            if (assets == null || assets.Config == null)
                return ArenaConfig.Default;
            var result = assets.Config.ToArenaConfig();
            if (result.IsSuccess)
                return result.Value;
            logger.Error("Invalid arena settings in " + assets.Config.name + " (" + result.Error + "). Falling back to defaults.");
            return ArenaConfig.Default;
        }

        private static QuizView CreateView(MathQuizAssets assets, Transform parent)
        {
            if (assets != null && assets.ViewPrefab != null)
                return Object.Instantiate(assets.ViewPrefab, parent);
            return QuizViewLayout.Create(parent);
        }
    }
}
