using System;
using System.Collections.Generic;
using System.Linq;

namespace TestGate.Planning
{
    public sealed class WorkItem
    {
        public string Feature { get; set; }
        public string Title { get; set; }
        public string Priority { get; set; }
        public List<string> Requirements { get; set; } = new List<string>();
        public List<string> Modules { get; set; } = new List<string>();
        public List<string> Why { get; set; } = new List<string>();
        public bool Rush { get; set; }
        public string Lane { get; set; }          // "now" | "after <id>" (same modules) | "blocked"
        public List<string> BlockedBy { get; set; } = new List<string>();

        public string Command =>
            "./tools/test-gate.sh task start \"" + Feature + ": " + Title + "\" " + string.Join(" ", Requirements.Select(r => "--req " + r));
    }

    /// <summary>
    /// "What can be done next, in parallel?" computed from the plan's gaps and dependencies; no LLM, no stored task list.
    ///   ready  = hard deps DONE; contract deps DONE, or (feature [rush] and interface frozen) → allowed WITH debt
    ///   lanes  = greedy by priority/severity; items whose modules overlap a chosen item or an open task wait ("after").
    /// </summary>
    public static class PlanScheduler
    {
        public static List<WorkItem> Next(PlanReport report, IEnumerable<RegisteredTask> openTasks = null)
        {
            var busy = (openTasks ?? Enumerable.Empty<RegisteredTask>()).ToList();
            var items = new List<WorkItem>();
            foreach (var f in report.Features.Where(x => x.Status != FeatureStatus.Done && x.Status != FeatureStatus.Deferred && x.Status != FeatureStatus.Dropped))
            {
                var gaps = report.Gaps.Where(g => g.Feature == f.Id && g.Actor == "agent" && g.Requirement != null).ToList();
                if (gaps.Count == 0)
                    continue;
                var item = new WorkItem
                {
                    Feature = f.Id, Title = f.Title, Priority = f.Priority, Modules = f.Modules.ToList(), Rush = f.Tags.Contains("rush"),
                    Requirements = gaps.OrderBy(g => g.Severity).Select(g => g.Requirement).Distinct().ToList(),
                    Why = gaps.OrderBy(g => g.Severity).Select(g => g.Kind + " " + g.Requirement).Distinct().ToList(),
                    BlockedBy = f.BlockedBy.ToList()
                };
                if (item.Rush && f.Debt.Count > 0)
                    item.Why.Add("RUSH: " + f.Debt.Count + " khoản nợ tích hợp sẽ được theo dõi");
                items.Add(item);
            }

            var order = items
                .OrderBy(i => i.BlockedBy.Count > 0 ? 1 : 0)
                .ThenBy(i => Rank(i.Priority))
                .ThenBy(i => report.Gaps.Where(g => g.Feature == i.Feature).Select(g => g.Severity).DefaultIfEmpty(9).Min())
                .ThenBy(i => report.Feature(i.Feature).Depth)
                .ThenBy(i => i.Feature, StringComparer.Ordinal)
                .ToList();

            var taken = new List<WorkItem>();
            foreach (var i in order)
            {
                if (i.BlockedBy.Count > 0)
                {
                    i.Lane = "blocked";
                    continue;
                }
                var clashTask = busy.FirstOrDefault(t => t.Modules.Intersect(i.Modules).Any());
                var clashItem = taken.FirstOrDefault(t => t.Modules.Intersect(i.Modules).Any());
                if (clashTask != null)
                    i.Lane = "after task " + clashTask.Id + " (cùng module " + string.Join(",", clashTask.Modules.Intersect(i.Modules)) + ")";
                else if (clashItem != null)
                    i.Lane = "after " + clashItem.Feature + " (cùng module " + string.Join(",", clashItem.Modules.Intersect(i.Modules)) + ")";
                else
                {
                    i.Lane = "now";
                    taken.Add(i);
                }
            }
            return order;
        }

        private static int Rank(string p) => p != null && p.Length == 2 && char.IsDigit(p[1]) ? p[1] - '0' : 9;
    }
}
