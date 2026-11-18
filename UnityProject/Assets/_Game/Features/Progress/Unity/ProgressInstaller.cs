using Game.Progress.Contracts;

namespace Game.Progress.Infrastructure
{
    /// <summary>
    /// Public face of the Progress feature for the composition root. Everything else is internal.
    /// </summary>
    public sealed class ProgressModule
    {
        private readonly IProgressStore _store;

        public IBestScoreService BestScore { get; }

        internal ProgressModule(IBestScoreService bestScore, IProgressStore store)
        {
            BestScore = bestScore;
            _store = store;
        }

        /// <summary>Writes staged saves to disk. Call on pause/quit.</summary>
        public void Flush() => _store.Flush();
    }

    public static class ProgressInstaller
    {
        /// <summary>PlayerPrefs key (public for the Editor "Reset Saved Progress" menu and tests).</summary>
        public const string PlayerPrefsKey = "game.progress.v1";

        public static ProgressModule Install()
        {
            var store = new PlayerPrefsProgressStore();
            var bestScore = new BestScoreService(store);
            bestScore.Load();
            return new ProgressModule(bestScore, store);
        }
    }
}
