using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Ads.Contracts
{
    /// <summary>
    /// What gameplay may know about ads. No SDK types leak through this interface, so the SDK can be
    /// replaced by changing only the adapter (Game.Ads.Unity).
    /// Callers: after awaiting, re-check your Lifetime/state before using the result (the player may have replayed).
    /// </summary>
    public interface IAdsService
    {
        /// <summary>True if the player watched the ad to the end. Resumes on the main thread. Cancelling abandons the wait.</summary>
        UniTask<bool> ShowRewardedAsync(CancellationToken cancellationToken);
    }
}
