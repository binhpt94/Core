using System;
using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>Ties a task to plan requirements: readiness at start, STALE when the human rewords/removes them mid-task.</summary>
    public static class TaskPlanLink
    {
        /// <summary>Requirements of the task whose wording changed (or that were removed, or became [critical] under a task without --role) since the task started.</summary>
        public static List<string> Stale(Plan plan, AgentTask task)
        {
            var result = new List<string>();
            if (plan == null || task == null || task.Requirements == null)
                return result;
            foreach (var kv in task.Requirements)
            {
                var r = plan.Requirement(kv.Key);
                if (r == null)
                    result.Add(kv.Key + " was removed from the plan");
                else if (r.Hash != kv.Value)
                    result.Add(kv.Key + " was changed in the plan (now: \"" + r.Text + "\")");
                else if (plan.Feature(r.Feature) is PlanFeature f && (f.Deferred || f.Dropped))
                    result.Add(kv.Key + ": feature " + f.Id + " is now " + (f.Dropped ? "dropped" : "deferred"));
                else if (r.IsCritical && task.Role == null)
                    result.Add(kv.Key + " is [critical] now: a task without --role cannot make it count. task close, then work it in 3 roles (roadmap next: --role tests, then --role impl in another session)");
            }
            return result;
        }

        /// <summary>`task start --retire`: only [dropped] requirements (or of a [dropped] feature); scope = their feature's modules.</summary>
        public static (List<string> modules, List<string> errors) PrepareRetire(string root, IEnumerable<string> ids)
        {
            var errors = new List<string>();
            var modules = new List<string>();
            var plan = PlanParser.Load(root);
            if (plan == null)
                return (modules, new List<string> { "no " + PlanParser.RelativePath + ": --retire needs a plan" });
            var dropped = new HashSet<string>(plan.DroppedRequirements.Select(r => r.Id), StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var r = plan.Requirement(id);
                if (r == null)
                    errors.Add(id + " is not in the plan (a removed requirement: the owner marks it [dropped] instead, so its leftovers can be found)");
                else if (!dropped.Contains(id))
                    errors.Add(id + " is not [dropped]: only the owner cancels a requirement (features.md, yes/no)");
                else
                    modules.AddRange(plan.Feature(r.Feature).Modules);
            }
            return (modules.Distinct().ToList(), errors);
        }

        /// <summary>Checks requirements for `task start --req`: exist, not blocked. Returns modules (scope) or errors.</summary>
        public static (List<string> modules, List<string> errors, List<string> notes) Prepare(string root, IEnumerable<string> reqIds)
        {
            var errors = new List<string>();
            var notes = new List<string>();
            var modules = new List<string>();
            var plan = PlanParser.Load(root);
            if (plan == null)
            {
                errors.Add("no " + PlanParser.RelativePath + ": --req needs a plan");
                return (modules, errors, notes);
            }
            var report = PlanStatus.Compute(PlanInputs.Load(root));
            foreach (var id in reqIds)
            {
                var r = plan.Requirement(id);
                if (r == null)
                {
                    errors.Add(id + " is not in the plan (propose it: roadmap propose \"...\")");
                    continue;
                }
                var f = plan.Feature(r.Feature);
                var fs = report.Feature(f.Id);
                if (!f.Active)
                    errors.Add(id + ": feature " + f.Id + " is " + (f.Dropped ? "dropped" : "deferred"));
                foreach (var b in fs.BlockedBy)
                    errors.Add(id + " (" + f.Id + ") is blocked: " + b);
                foreach (var d in fs.Debt)
                    notes.Add("RUSH " + f.Id + ": " + d);
                modules.AddRange(f.Modules);
            }
            return (modules.Distinct().ToList(), errors.Distinct().ToList(), notes.Distinct().ToList());
        }
    }
}
