using UnityEngine;

namespace Game.Progress.Infrastructure
{
    /// <summary>
    /// Stores progress as JSON in PlayerPrefs. Save() only stages in memory;
    /// Flush() writes to disk (called by the composition root on pause/quit).
    /// </summary>
    internal sealed class PlayerPrefsProgressStore : IProgressStore
    {
        private const string Key = ProgressInstaller.PlayerPrefsKey;

        private bool _dirty;

        public ProgressData Load()
        {
            if (!PlayerPrefs.HasKey(Key))
                return null;

            try
            {
                return JsonUtility.FromJson<ProgressData>(PlayerPrefs.GetString(Key));
            }
            catch (System.ArgumentException)
            {
                // Corrupted JSON: let ProgressMigrator fall back to defaults.
                return null;
            }
        }

        public void Save(ProgressData data)
        {
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(data));
            _dirty = true;
        }

        public void Flush()
        {
            if (!_dirty)
                return;

            PlayerPrefs.Save();
            _dirty = false;
        }
    }
}
