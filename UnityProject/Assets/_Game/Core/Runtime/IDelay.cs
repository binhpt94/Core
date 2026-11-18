using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Core
{
    /// <summary>
    /// Time port. Logic never calls UniTask.Delay / Task.Delay directly:
    /// - in the game it is implemented with UniTask.Delay on the PlayerLoop (respects Time.timeScale),
    /// - in tests by ManualDelay, which the test advances explicitly (no real waiting, no flakiness).
    /// Implementations must throw OperationCanceledException (or return a canceled task) when canceled.
    /// </summary>
    public interface IDelay
    {
        UniTask Delay(TimeSpan duration, CancellationToken cancellationToken);
    }
}
