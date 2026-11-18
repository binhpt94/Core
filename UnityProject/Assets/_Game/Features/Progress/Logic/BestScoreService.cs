using Game.Progress.Contracts;

namespace Game.Progress
{
    internal sealed class BestScoreService : IBestScoreService
    {
        private readonly IProgressStore _store;
        private ProgressData _data;

        public int BestScore => _data.BestScore;

        /// <summary>
        /// Constructor has no side effects. Call <see cref="Load"/> explicitly from the composition root.
        /// </summary>
        public BestScoreService(IProgressStore store)
        {
            _store = store ?? throw new System.ArgumentNullException(nameof(store));
            _data = new ProgressData();
        }

        public void Load()
        {
            _data = ProgressMigrator.Migrate(_store.Load());
        }

        public bool SubmitScore(int score)
        {
            if (score <= _data.BestScore)
                return false;

            _data.BestScore = score;
            _store.Save(_data);
            return true;
        }
    }
}
