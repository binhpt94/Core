using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Core;
using UnityEngine;

namespace Game.Bootstrap
{
    /// <summary>
    /// Scaled game time (stops with Time.timeScale = 0). Unity clamps a long frame (e.g. resuming from
    /// background) with Time.maximumDeltaTime, and TickScheduler clamps again: the game is effectively paused.
    /// </summary>
    public sealed class UnityGameClock : IGameClock
    {
        public long NowMicroseconds => (long)(Time.timeAsDouble * 1_000_000.0);
    }

    /// <summary>One await per rendered frame, on the PlayerLoop (stops when Play Mode ends).</summary>
    public sealed class UnityFrameSource : IFrameSource
    {
        public UniTask NextFrame(CancellationToken cancellationToken) =>
            UniTask.NextFrame(PlayerLoopTiming.Update, cancellationToken);
    }
}
