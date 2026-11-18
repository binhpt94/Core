using System;

namespace Game.Core
{
    /// <summary>
    /// xoshiro128** (Blackman &amp; Vigna) seeded through SplitMix64, with Lemire's unbiased bounded ints.
    /// Pure integer arithmetic: identical results on CoreCLR, Mono and IL2CPP. Frozen by test vectors
    /// (DeterministicRandomTests) computed from an independent reference implementation.
    /// </summary>
    public sealed class DeterministicRandom : IRandom
    {
        public const string Algorithm = "xoshiro128**+splitmix64";

        private uint _s0, _s1, _s2, _s3;

        public DeterministicRandom(ulong seed)
        {
            ulong x = seed;
            ulong a = SplitMix64(ref x);
            ulong b = SplitMix64(ref x);
            _s0 = (uint)a;
            _s1 = (uint)(a >> 32);
            _s2 = (uint)b;
            _s3 = (uint)(b >> 32);
            if ((_s0 | _s1 | _s2 | _s3) == 0)
                _s0 = 1; // all-zero state is the one invalid xoshiro state
        }

        public uint NextUInt()
        {
            uint result = Rotl(_s1 * 5, 7) * 9;
            uint t = _s1 << 9;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = Rotl(_s3, 11);
            return result;
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be > minInclusive");

            uint range = (uint)((long)maxExclusive - minInclusive);
            uint x = NextUInt();
            ulong m = (ulong)x * range;
            uint low = (uint)m;
            if (low < range)
            {
                uint threshold = unchecked(0u - range) % range;
                while (low < threshold)
                {
                    x = NextUInt();
                    m = (ulong)x * range;
                    low = (uint)m;
                }
            }
            return (int)(minInclusive + (long)(m >> 32));
        }

        internal static ulong SplitMix64(ref ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));
    }
}
