using Game.Core;
using UnityEngine;

namespace Game.Bootstrap
{
    /// <summary>Unity console adapter for <see cref="IGameLogger"/>.</summary>
    public sealed class UnityGameLogger : IGameLogger
    {
        private const string Prefix = "[Game] ";

        public void Info(string message) => Debug.Log(Prefix + message);
        public void Warning(string message) => Debug.LogWarning(Prefix + message);
        public void Error(string message) => Debug.LogError(Prefix + message);
    }
}
