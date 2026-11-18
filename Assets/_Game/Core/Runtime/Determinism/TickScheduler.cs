using System;

namespace Game.Core
{
    /// <summary>
    /// Fixed-timestep accumulator: converts elapsed game time into a number of simulation ticks to run this frame.
    /// The simulation result depends only on tick numbers and the commands applied at each tick, never on FPS.
    /// Policy (explicit, never silent tick dropping):
    ///  - at most <see cref="MaxTicksPerFrame"/> ticks per frame; the remaining backlog is kept and caught up later;
    ///  - a single frame longer than <see cref="MaxFrameMicroseconds"/> (hitch, app resumed from background) is clamped:
    ///    the extra wall time is NOT simulated, i.e. the game was effectively paused (fair for an offline game).
    /// Pure integer microseconds: deterministic for a given sequence of clock readings.
    /// </summary>
    public sealed class TickScheduler
    {
        public int TickRate { get; }
        public long TickMicroseconds { get; }
        public int MaxTicksPerFrame { get; }
        public long MaxFrameMicroseconds { get; }

        private long _last = -1;
        private long _accumulated;

        public TickScheduler(int tickRate, int maxTicksPerFrame = 8, long maxFrameMicroseconds = 250_000)
        {
            if (tickRate <= 0 || 1_000_000 % tickRate != 0)
                throw new ArgumentOutOfRangeException(nameof(tickRate), "tick rate must divide 1,000,000 (e.g. 10, 20, 25, 50)");
            TickRate = tickRate;
            TickMicroseconds = 1_000_000 / tickRate;
            MaxTicksPerFrame = maxTicksPerFrame;
            MaxFrameMicroseconds = maxFrameMicroseconds;
        }

        public long BacklogMicroseconds => _accumulated;

        /// <summary>Call once per frame with the current game time; returns how many ticks to step.</summary>
        public int Advance(long nowMicroseconds)
        {
            if (_last < 0)
            {
                _last = nowMicroseconds;
                return 0;
            }

            long delta = nowMicroseconds - _last;
            _last = nowMicroseconds;
            if (delta < 0)
                delta = 0;
            if (delta > MaxFrameMicroseconds)
                delta = MaxFrameMicroseconds;

            _accumulated += delta;
            long due = _accumulated / TickMicroseconds;
            int run = (int)Math.Min(due, MaxTicksPerFrame);
            _accumulated -= run * TickMicroseconds;
            return run;
        }
    }
}
