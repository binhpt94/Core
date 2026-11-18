using System.Text;

namespace Game.Core
{
    /// <summary>
    /// Keyed random streams: For("arena.move", enemyId, tick) always yields the same numbers for the same
    /// (session seed, stream, keys), whatever else consumed randomness before. Adding a random call in one
    /// system therefore never shifts the results of another ("RNG consumption drift").
    /// Stream names are part of the save/replay format: never rename them casually.
    /// </summary>
    public interface IRandomStreams
    {
        ulong Seed { get; }

        IRandom For(string stream, int key1 = 0, int key2 = 0, int key3 = 0);
    }

    public sealed class RandomStreams : IRandomStreams
    {
        /// <summary>Recorded in replays; changing any part of the derivation must bump it.</summary>
        public const string Version = DeterministicRandom.Algorithm + "+fnv1a64/v1";

        public ulong Seed { get; }

        public RandomStreams(ulong seed) => Seed = seed;

        public IRandom For(string stream, int key1 = 0, int key2 = 0, int key3 = 0) =>
            new DeterministicRandom(Derive(Seed, stream, key1, key2, key3));

        /// <summary>FNV-1a 64 over (seed LE, UTF-8 stream, 0, keys LE). Never string.GetHashCode(): it changes per process.</summary>
        public static ulong Derive(ulong seed, string stream, int key1, int key2, int key3)
        {
            ulong h = 0xCBF29CE484222325UL;
            for (int i = 0; i < 8; i++)
                h = Mix(h, (byte)(seed >> (8 * i)));
            foreach (byte b in Encoding.UTF8.GetBytes(stream))
                h = Mix(h, b);
            h = Mix(h, 0);
            h = MixInt(MixInt(MixInt(h, key1), key2), key3);
            return h;
        }

        private static ulong MixInt(ulong h, int v)
        {
            uint u = unchecked((uint)v);
            for (int i = 0; i < 4; i++)
                h = Mix(h, (byte)(u >> (8 * i)));
            return h;
        }

        private static ulong Mix(ulong h, byte b) => (h ^ b) * 0x100000001B3UL;
    }
}
