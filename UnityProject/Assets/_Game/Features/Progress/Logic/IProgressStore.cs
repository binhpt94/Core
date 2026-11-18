namespace Game.Progress
{
    /// <summary>
    /// Storage port. Implemented in Game.Progress.Unity (PlayerPrefs) and by fakes in tests.
    /// </summary>
    internal interface IProgressStore
    {
        /// <summary>Returns stored data, or null if nothing is stored yet or the data is unreadable.</summary>
        ProgressData Load();

        /// <summary>Stages data in memory. Cheap; may be called often.</summary>
        void Save(ProgressData data);

        /// <summary>Writes staged data to disk. Expensive; call on pause/quit, not every change.</summary>
        void Flush();
    }
}
