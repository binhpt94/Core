using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Game.Core
{
    /// <summary>
    /// A simulation that can be replayed: commands are text (feature-defined), applied at the start of the next Step().
    /// </summary>
    public interface IReplayableSimulation
    {
        long Tick { get; }

        /// <summary>Queues a command (text form) for the next tick. Returns false if the text is not a valid command.</summary>
        bool Enqueue(string command);

        void Step();

        string StateHash();
    }

    /// <summary>
    /// Replay record: everything needed to reproduce a session bit-for-bit.
    /// Text, one entry per line (cheap to read for humans and agents, diff-friendly):
    ///   replay 1
    ///   meta seed=912734 tickRate=20 sim=1 rng=... config=...
    ///   cmd 37 Answer 1        command applied at tick 37
    ///   hash 40 9c1e2a3b4c5d6e7f   state hash after tick 40
    ///   end 600 0123456789abcdef
    /// Seed alone is NOT enough: the commands (with their ticks), the versions and the config are part of it.
    /// </summary>
    public sealed class ReplayLog
    {
        public const int FormatVersion = 1;

        public sealed class Entry
        {
            public long Tick { get; }
            /// <summary>Command text, or null for a hash checkpoint.</summary>
            public string Command { get; }
            public string Hash { get; }

            public Entry(long tick, string command, string hash)
            {
                Tick = tick;
                Command = command;
                Hash = hash;
            }
        }

        public SortedDictionary<string, string> Meta { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public List<Entry> Entries { get; } = new List<Entry>();
        public long EndTick { get; private set; }
        public string EndHash { get; private set; }

        /// <summary>Keep at most this many entries (ring buffer for long sessions / bug reports). 0 = unlimited.</summary>
        public int Capacity { get; set; }

        /// <summary>True if old entries were dropped: the log can then only be replayed from a saved snapshot.</summary>
        public bool Truncated { get; private set; }

        public void RecordCommand(long tick, string command) => Add(new Entry(tick, command, null));

        public void RecordHash(long tick, string hash) => Add(new Entry(tick, null, hash));

        public void End(long tick, string hash)
        {
            EndTick = tick;
            EndHash = hash;
        }

        private void Add(Entry e)
        {
            Entries.Add(e);
            if (Capacity > 0 && Entries.Count > Capacity)
            {
                Entries.RemoveAt(0);
                Truncated = true;
            }
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("replay ").Append(FormatVersion).Append('\n');
            sb.Append("meta");
            foreach (var kv in Meta)
                sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            sb.Append('\n');
            foreach (var e in Entries)
                sb.Append(e.Command != null ? "cmd " + e.Tick + " " + e.Command : "hash " + e.Tick + " " + e.Hash).Append('\n');
            if (EndHash != null)
                sb.Append("end ").Append(EndTick).Append(' ').Append(EndHash).Append('\n');
            return sb.ToString();
        }

        public static ReplayLog Parse(string text)
        {
            var log = new ReplayLog();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(new[] { ' ' }, 3);
                switch (parts[0])
                {
                    case "replay":
                        if (parts.Length < 2 || parts[1] != FormatVersion.ToString(CultureInfo.InvariantCulture))
                            throw new FormatException("line " + (i + 1) + ": unsupported replay format " + line);
                        break;
                    case "meta":
                        foreach (var pair in line.Substring(4).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = pair.IndexOf('=');
                            if (eq <= 0)
                                throw new FormatException("line " + (i + 1) + ": bad meta '" + pair + "'");
                            log.Meta[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        }
                        break;
                    case "cmd":
                        log.RecordCommand(ParseTick(parts, i), parts.Length > 2 ? parts[2] : throw new FormatException("line " + (i + 1) + ": cmd without command"));
                        break;
                    case "hash":
                        log.RecordHash(ParseTick(parts, i), parts.Length > 2 ? parts[2] : throw new FormatException("line " + (i + 1) + ": hash without value"));
                        break;
                    case "end":
                        log.End(ParseTick(parts, i), parts.Length > 2 ? parts[2] : null);
                        break;
                    default:
                        throw new FormatException("line " + (i + 1) + ": unknown entry '" + parts[0] + "'");
                }
            }
            return log;
        }

        private static long ParseTick(string[] parts, int line) =>
            parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                ? t
                : throw new FormatException("line " + (line + 1) + ": bad tick");
    }

    public sealed class ReplayResult
    {
        public bool Matches { get; internal set; }
        public long DivergedAtTick { get; internal set; } = -1;
        public string Expected { get; internal set; }
        public string Actual { get; internal set; }
        public string Message { get; internal set; }
        public IReplayableSimulation Simulation { get; internal set; }

        public override string ToString() => Matches ? "replay OK (" + Message + ")" : "replay DIVERGED: " + Message;
    }

    /// <summary>Re-runs a replay and reports the FIRST tick whose state hash differs (or a rejected command).</summary>
    public static class ReplayRunner
    {
        public static ReplayResult Run(ReplayLog log, Func<IReadOnlyDictionary<string, string>, IReplayableSimulation> create)
        {
            if (log.Truncated)
                return new ReplayResult { Message = "log was truncated (ring buffer); cannot replay from the start" };

            var sim = create(log.Meta);
            var byTick = log.Entries.GroupBy(e => e.Tick).ToDictionary(g => g.Key, g => g.ToList());
            long last = Math.Max(log.EndTick, log.Entries.Count == 0 ? 0 : log.Entries.Max(e => e.Tick));

            while (sim.Tick < last)
            {
                long next = sim.Tick + 1;
                if (byTick.TryGetValue(next, out var entries))
                    foreach (var c in entries.Where(e => e.Command != null))
                        if (!sim.Enqueue(c.Command))
                            return Fail(sim, next, null, null, "tick " + next + ": unknown command '" + c.Command + "'");

                sim.Step();

                if (byTick.TryGetValue(sim.Tick, out entries))
                    foreach (var h in entries.Where(e => e.Command == null))
                    {
                        string actual = sim.StateHash();
                        if (actual != h.Hash)
                            return Fail(sim, sim.Tick, h.Hash, actual, "state hash differs at tick " + sim.Tick + " (expected " + h.Hash + ", got " + actual + ")");
                    }
            }

            if (log.EndHash != null && sim.StateHash() != log.EndHash)
                return Fail(sim, sim.Tick, log.EndHash, sim.StateHash(), "final hash differs at tick " + sim.Tick);

            return new ReplayResult { Matches = true, Simulation = sim, Message = sim.Tick + " ticks, " + log.Entries.Count(e => e.Command != null) + " commands" };
        }

        private static ReplayResult Fail(IReplayableSimulation sim, long tick, string expected, string actual, string message) =>
            new ReplayResult { Matches = false, DivergedAtTick = tick, Expected = expected, Actual = actual, Message = message, Simulation = sim };
    }
}
