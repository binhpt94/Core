using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Core
{
    /// <summary>
    /// Game time in microseconds (scaled: stops when the game is paused). Only the simulation runner reads it,
    /// to decide HOW MANY fixed ticks to run; simulation logic itself only ever sees integer tick numbers.
    /// </summary>
    public interface IGameClock
    {
        long NowMicroseconds { get; }
    }

    /// <summary>Awaits the next rendered frame (Unity: PlayerLoop Update; tests: ManualFrames).</summary>
    public interface IFrameSource
    {
        UniTask NextFrame(CancellationToken cancellationToken);
    }
}
