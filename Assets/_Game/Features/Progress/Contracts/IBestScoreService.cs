namespace Game.Progress.Contracts
{
    /// <summary>
    /// Public API of the Progress feature. Other features depend ONLY on this contract,
    /// never on Game.Progress (Logic) or Game.Progress.Unity.
    /// </summary>
    public interface IBestScoreService
    {
        int BestScore { get; }

        /// <summary>Records a finished round score. Returns true if it is a new best.</summary>
        bool SubmitScore(int score);
    }
}
