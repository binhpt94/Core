using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>What the dashboard shows for the plan: all computed (plan + tests + ledger + registry), nothing stored.</summary>
    public sealed class RoadmapView
    {
        public bool HasPlan { get; set; }
        public string Title { get; set; }
        public string ResultsUtc { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<FeatureState> Features { get; set; } = new List<FeatureState>();
        public List<Gap> Gaps { get; set; } = new List<Gap>();
        public List<WorkItem> Next { get; set; } = new List<WorkItem>();
        public List<string> Proposals { get; set; } = new List<string>();
        public List<RegisteredTask> OpenTasks { get; set; } = new List<RegisteredTask>();
        public List<SlotState> Slots { get; set; } = new List<SlotState>();
        /// <summary>Work for artists / the human (not agents): art debt, approvals, feasibility.</summary>
        public List<Gap> ArtLane { get; set; } = new List<Gap>();
        /// <summary>Change requests between modules not closed yet (who decides an open one: gate / arbiter / owner).</summary>
        public List<CrView> Crs { get; set; } = new List<CrView>();
        /// <summary>Decisions only the owner can make (CRs to decide, overdue CRs, proposals, manual reviews): "Cần bạn quyết".</summary>
        public List<Gap> Owner { get; set; } = new List<Gap>();

        public static RoadmapView Build(string root)
        {
            if (PlanParser.Load(root) == null)
                return new RoadmapView();
            var inputs = PlanInputs.Load(root);
            var report = PlanStatus.Compute(inputs);
            var open = TaskRegistry.Load(root).Where(t => t.Status != AgentTask.Closed).ToList();
            return new RoadmapView
            {
                HasPlan = true, Title = report.Title, ResultsUtc = report.ResultsUtc, Errors = report.Errors,
                Features = report.Features, Gaps = report.Gaps, Next = PlanScheduler.Next(report, open),
                Proposals = report.Proposals, OpenTasks = open,
                Slots = inputs.Slots.Values.OrderBy(x => x.Id, System.StringComparer.Ordinal).ToList(),
                ArtLane = report.Gaps.Where(g => g.Actor == "art" || (g.Actor == "human" && g.Kind == ReqStatus.NeedsReview)).ToList(),
                Crs = report.Crs,
                Owner = report.Gaps.Where(g => g.Actor == "human").Concat(MessageTrace.Gaps(root)).ToList()
            };
        }
    }
}
