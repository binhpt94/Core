using System;
using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Behaviour change without provenance, measured by the tests themselves: in a maintenance task (no --req/--from), a
    /// requirement's covering test that was red when the task started and is green after the gate means the task delivered
    /// behaviour no requirement or CR asked it to (typically: another session asked by message). Files alone can't show it.
    /// </summary>
    public static class Provenance
    {
        public const string Pass = "pass", Fail = "fail";

        /// <summary>Latest result of every [Covers] test in these feature folders: test key → pass | fail (no result = absent).</summary>
        public static Dictionary<string, string> CoverResults(string root, IEnumerable<string> features)
        {
            var dirs = features.Select(f => ScopePolicy.Game + "Features/" + f + "/").ToList();
            var results = TestResultsIndex.Load(root);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in PlanRules.Covers(root).Where(c => dirs.Any(d => c.File.StartsWith(d, StringComparison.Ordinal))))
            {
                var last = results.Find(c.TestKey).OrderBy(o => o.RunUtc).LastOrDefault();
                if (last != null)
                    map[c.TestKey + " (" + c.Requirement + ")"] = last.Passed ? Pass : Fail;
            }
            return map;
        }

        /// <summary>Covering tests red at the start, green now.</summary>
        public static List<string> TurnedGreen(IDictionary<string, string> before, IDictionary<string, string> after) =>
            after.Where(kv => kv.Value == Pass && before != null && before.TryGetValue(kv.Key, out var b) && b == Fail).Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();

        /// <summary>Stop, after the gate ran: findings for a maintenance task whose changes turned requirement tests green.</summary>
        public static List<string> Findings(string root, AgentTask task)
        {
            if (task == null || !task.IsActive || task.Provenance != "maintenance" || task.CoverBaseline == null)
                return new List<string>();
            return TurnedGreen(task.CoverBaseline, CoverResults(root, task.Features))
                .Select(k => "UNTRACED_CHANGE test " + k + " was red when task " + task.Id + " started and is green now: this task changed behaviour " +
                             "without a requirement. Start it from the plan or a CR (task start … --req R-…|--from CR-n); another module asking for it files `task need`.")
                .ToList();
        }
    }
}
