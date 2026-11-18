namespace Game.Progress
{
    /// <summary>
    /// Persisted progress. Plain serializable class (works with UnityEngine.JsonUtility):
    /// public fields, parameterless constructor, no properties.
    /// Bump <see cref="CurrentVersion"/> and extend <see cref="ProgressMigrator"/> when the shape changes.
    /// </summary>
    [System.Serializable]
    internal sealed class ProgressData
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public int BestScore;
    }

    internal static class ProgressMigrator
    {
        /// <summary>
        /// Brings loaded data up to the current version and repairs invalid values.
        /// Never throws: corrupted saves must not crash the game at startup.
        /// </summary>
        public static ProgressData Migrate(ProgressData data)
        {
            if (data == null || data.Version <= 0 || data.Version > ProgressData.CurrentVersion)
                return new ProgressData();

            // Future: if (data.Version == 1) { ...upgrade...; data.Version = 2; }

            if (data.BestScore < 0)
                data.BestScore = 0;

            return data;
        }
    }
}
