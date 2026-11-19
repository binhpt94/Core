using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    /// <summary>
    /// "A messaged someone at t, then another session changed a feature in a task with no requirement": circumstantial evidence of a
    /// change asked for by chat instead of a CR. The receiver can't be named (a message's recipient is an agent name, not a session),
    /// so this is shown to the owner on the dashboard, never blocking; the hard check is provenance at the receiver's Stop.
    /// </summary>
    public static class MessageTrace
    {
        public static readonly TimeSpan Window = TimeSpan.FromHours(2);

        public sealed class Pair
        {
            public AgentEvent Message { get; set; }
            public AgentEvent Edit { get; set; }

            public string Text =>
                "phiên " + Short(Message.Session) + (Message.Get("fromTask") != null ? " (task " + Message.Get("fromTask") + ")" : "") + " nhắn " +
                (Message.Get("to") ?? "?") + " lúc " + Hm(Message.Ts) + ": \"" + (Message.Get("summary") ?? "") + "\" → phiên " + Short(Edit.Session) +
                " sửa " + Edit.Get("path") + " lúc " + Hm(Edit.Ts) + " trong task " + Edit.Task + " không có R-/CR";
        }

        /// <summary>Pairs across these event logs (one per worktree: a message and the edit it caused may be in different worktrees).</summary>
        public static List<Pair> Pairs(IEnumerable<IList<AgentEvent>> logs)
        {
            var all = logs.SelectMany(l => l).ToList();
            // A task has provenance if it started with requirements (or a CR) or is core maintenance.
            var traced = new HashSet<string>(all.Where(e => e.Type == EventTypes.TaskCreated && e.Get("requirements") != null).Select(e => e.Get("id")), StringComparer.Ordinal);
            foreach (var e in all.Where(e => e.Type == EventTypes.TaskApproved && (e.Get("scope") ?? "").Contains("core maintenance")))
                traced.Add(e.Get("id"));
            var messages = all.Where(e => e.Type == EventTypes.AgentMessage).ToList();
            var edits = all.Where(e => e.Type == EventTypes.FileModified && e.Get("inScope") == "true" && e.Task != null && !traced.Contains(e.Task)
                                       && (e.Get("path") ?? "").StartsWith(ScopePolicy.Game + "Features/", StringComparison.Ordinal)).ToList();
            var pairs = new List<Pair>();
            foreach (var m in messages)
            {
                if (!TryTime(m.Ts, out var t))
                    continue;
                foreach (var g in edits.Where(e => e.Session != m.Session && TryTime(e.Ts, out var te) && te > t && te - t <= Window).GroupBy(e => e.Session))
                    pairs.Add(new Pair { Message = m, Edit = g.OrderBy(e => e.Ts, StringComparer.Ordinal).First() });
            }
            return pairs;
        }

        /// <summary>The dashboard's owner list: event logs of this worktree and of every worktree with an open task.</summary>
        public static List<Gap> Gaps(string root)
        {
            var roots = new[] { Path.GetFullPath(root) }.Concat(TaskRegistry.Load(root).Select(t => Path.GetFullPath(t.Worktree))).Distinct(StringComparer.OrdinalIgnoreCase);
            var logs = new List<IList<AgentEvent>>();
            foreach (var r in roots)
            {
                try { logs.Add(EventLog.For(r).ReadAll()); }
                catch (IOException) { }
            }
            return Pairs(logs).Select(p => new Gap { Kind = "MESSAGE_THEN_EDIT", Actor = "human", Severity = 5, Feature = null, Detail = p.Text }).ToList();
        }

        private static bool TryTime(string ts, out DateTime t) =>
            DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);

        private static string Hm(string ts) => ts != null && ts.Length >= 16 ? ts.Substring(11, 5) : ts;

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : s.Length > 8 ? s.Substring(0, 8) : s;
    }
}
