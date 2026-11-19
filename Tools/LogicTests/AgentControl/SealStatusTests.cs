using System;
using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>What a requirement's status says once tests are written and sealed by another session (separation of duties).</summary>
    public sealed class SealStatusTests
    {
        private static readonly DateTime Run = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        private const string Plan = "## F-A  A  [P0] [modules: M]\n- R-1  one\n- R-2  two\n";

        private static PlanInputs Inputs(bool r1Passes = true)
        {
            var results = new TestResultsIndex();
            results.Add(new TestOutcome { Key = "ATests.One", Passed = r1Passes, RunUtc = Run });
            var plan = PlanParser.Parse(Plan);
            var i = new PlanInputs
            {
                Plan = plan, Results = results, ModuleChangedUtc = _ => Run.AddMinutes(-5),
                Covers = new List<CoverEntry> { new CoverEntry { Requirement = "R-1", Class = "ATests", Method = "One", File = "a.cs" } },
                SealHash = _ => "h1"
            };
            i.Seals["R-1"] = new SealState { Requirement = "R-1", Feature = "F-A", Hash = "h1", RequirementHash = plan.Requirement("R-1").Hash, Author = "sA", Red = "1/1", Commit = "c" };
            return i;
        }

        private static RequirementState R1(PlanInputs i) => PlanStatus.Compute(i).Requirement("R-1");

        [Test]
        public void PassingTests_CountOnlyWhenSealedByAnotherSession_UnchangedAndUndisputed()
        {
            Assert.That(R1(Inputs()).Status, Is.EqualTo(ReqStatus.Verified));

            var unsealed = Inputs();
            unsealed.Seals.Clear();
            Assert.That(R1(unsealed).Status, Is.EqualTo(ReqStatus.Unsealed), "passing tests nobody independent sealed prove nothing");
            Assert.That(R1(unsealed).Role, Is.EqualTo(TestRoles.Tests));

            var tampered = Inputs();
            tampered.SealHash = _ => "h2";
            Assert.That(R1(tampered).Status, Is.EqualTo(ReqStatus.Tampered));

            var disputed = Inputs();
            disputed.Seals["R-1"].OpenDispute = true;
            Assert.That(R1(disputed).Status, Is.EqualTo(ReqStatus.Disputed));
            Assert.That(R1(disputed).Role, Is.EqualTo(TestRoles.Arbiter));
            Assert.That(PlanStatus.Compute(disputed).Gaps.Single(g => g.Requirement == "R-1").Actor, Is.EqualTo("agent"), "an arbiter's work, not the owner's");

            disputed.Seals["R-1"].Ask = true;
            Assert.That(PlanStatus.Compute(disputed).Gaps.Single(g => g.Requirement == "R-1").Actor, Is.EqualTo("human"), "escalated: the owner rewords it");

            var reworded = Inputs();
            reworded.Plan = PlanParser.Parse(Plan.Replace("R-1  one", "R-1  one, reworded"));
            Assert.That(R1(reworded).Status, Is.EqualTo(ReqStatus.Unsealed), "a seal is for the wording it was written against");

            var pending = Inputs();
            pending.Seals["R-1"].Red = "pending";
            Assert.That(R1(pending).Status, Is.EqualTo(ReqStatus.Unsealed), "Unity-only tests: red first must be confirmed before they count");
        }

        [Test]
        public void Lanes_TestsAndCodeOfOneFeature_RunSideBySide()
        {
            var r = PlanStatus.Compute(Inputs(r1Passes: false));      // R-1 sealed and red → impl; R-2 has no test → tests
            var next = PlanScheduler.Next(r);
            Assert.That(next.Select(x => x.Role + ":" + x.Lane), Is.EquivalentTo(new[] { "impl:now", "tests:now" }));
            Assert.That(next.Single(x => x.Role == TestRoles.Impl).Command, Does.Contain("--role impl --req R-1"));
            Assert.That(next.Single(x => x.Role == TestRoles.Tests).Command, Does.Contain("--role tests --req R-2"));

            var busy = new[] { new RegisteredTask { Id = "T9", Modules = { "M" }, Role = TestRoles.Impl, Status = AgentTask.Active } };
            var withImpl = PlanScheduler.Next(r, busy);
            Assert.That(withImpl.Single(x => x.Role == TestRoles.Impl).Lane, Does.StartWith("after task T9"), "two impl sessions on one module still wait");
            Assert.That(withImpl.Single(x => x.Role == TestRoles.Tests).Lane, Is.EqualTo("now"), "the tests session never waits for the impl one");
        }
    }
}
