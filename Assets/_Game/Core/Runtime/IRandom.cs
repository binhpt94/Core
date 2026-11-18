namespace Game.Core
{
    /// <summary>
    /// Randomness port. Implementations must be deterministic for a given seed on every runtime
    /// (.NET, Mono, IL2CPP): use <see cref="DeterministicRandom"/> / <see cref="RandomStreams"/>, never
    /// System.Random (its algorithm is not guaranteed across .NET versions) or UnityEngine.Random.
    /// </summary>
    public interface IRandom
    {
        /// <summary>Returns an int in [minInclusive, maxExclusive), unbiased.</summary>
        int Next(int minInclusive, int maxExclusive);
    }
}
