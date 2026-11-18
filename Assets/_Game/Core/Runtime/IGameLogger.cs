namespace Game.Core
{
    /// <summary>
    /// Logging abstraction. Named IGameLogger to avoid clashing with UnityEngine.ILogger.
    /// </summary>
    public interface IGameLogger
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>Logger that discards everything. Handy default for tests.</summary>
    public sealed class NullGameLogger : IGameLogger
    {
        public static readonly NullGameLogger Instance = new NullGameLogger();

        private NullGameLogger() { }

        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
