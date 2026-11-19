using System.Linq;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// Planning tests that are about evidence (pass/fail/not run/stale…), not about who wrote the tests: every covered requirement
    /// gets a live seal by another session whose tests are unchanged, so the separation-of-duties check is satisfied and stays out of the way.
    /// </summary>
    public static class SealFixture
    {
        public static PlanInputs SealAll(PlanInputs i)
        {
            foreach (var r in i.Plan.AllRequirements.Where(r => i.Covers.Any(c => c.Requirement == r.Id)))
                i.Seals[r.Id] = new SealState { Requirement = r.Id, Feature = r.Feature, Hash = "sealed", RequirementHash = r.Hash, Author = "tests-session", Red = "1/1", Commit = "c0" };
            i.SealHash = _ => "sealed";
            return i;
        }
    }
}
