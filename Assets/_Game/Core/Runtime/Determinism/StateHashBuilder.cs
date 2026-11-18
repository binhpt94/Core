using System.Text;

namespace Game.Core
{
    /// <summary>
    /// Canonical, cross-runtime state hash (FNV-1a 64). Add fields in a FIXED order, collections sorted by a
    /// stable id, integers only (no floats, no GetHashCode, no object ids, no wall-clock time, no view state).
    /// Used for replay checkpoints: the first tick whose hash differs is where two runs diverged.
    /// </summary>
    public sealed class StateHashBuilder
    {
        private ulong _h = 0xCBF29CE484222325UL;

        public StateHashBuilder Add(long value)
        {
            ulong u = unchecked((ulong)value);
            for (int i = 0; i < 8; i++)
                _h = (_h ^ (byte)(u >> (8 * i))) * 0x100000001B3UL;
            return this;
        }

        public StateHashBuilder Add(bool value) => Add(value ? 1L : 0L);

        public StateHashBuilder Add(string value)
        {
            if (value == null)
                return Add(-1L);
            var bytes = Encoding.UTF8.GetBytes(value);
            Add(bytes.Length);
            foreach (var b in bytes)
                _h = (_h ^ b) * 0x100000001B3UL;
            return this;
        }

        public ulong Value => _h;

        public string Hex => _h.ToString("x16");
    }
}
