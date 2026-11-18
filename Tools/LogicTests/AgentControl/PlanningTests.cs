using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    public sealed class PlanParserTests
    {
        [Test]
        public void ParsesFeaturesRequirementsTagsAndDependencies()
        {
            var p = PlanParser.Parse("# Plan: X\nintro text\n## F-A  Alpha [P0] [modules: M1, M2] [rush]\nDesc line\n- R-A-1  Do it  [manual]\ndeps: hard F-B · contract F-C (IThing) ; soft F-D\n## F-B  Beta\n## F-C  C [deferred]\n## F-D  D [dropped]\n");
            Assert.That(p.Errors, Is.Empty, string.Join("\n", p.Errors));
            var a = p.Feature("F-A");
            Assert.That(a.Priority, Is.EqualTo("P0"));
            Assert.That(a.Modules, Is.EqualTo(new[] { "M1", "M2" }));
            Assert.That(a.Rush, Is.True);
            Assert.That(a.Description, Is.EqualTo(new[] { "Desc line" }));
            Assert.That(p.Requirement("R-A-1").IsManual, Is.True);
            Assert.That(a.Dependencies.Select(d => d.Kind + ":" + d.Feature + ":" + d.Contract), Is.EqualTo(new[] { "Hard:F-B:", "Contract:F-C:IThing", "Soft:F-D:" }));
            Assert.That(p.Feature("F-B").Priority, Is.EqualTo("P2"), "default priority");
            Assert.That(p.Feature("F-C").Deferred && p.Feature("F-D").Dropped, Is.True);
        }

        [Test]
        public void RequirementHash_ChangesWithWordingOrTags_NotWithTitle()
        {
            string h(string plan) => PlanParser.Parse(plan).Requirement("R-1").Hash;
            string baseline = h("## F-A  Alpha\n- R-1  Show the timer\n");
            Assert.That(h("## F-A  Renamed feature\n- R-1  Show the timer\n"), Is.EqualTo(baseline));
            Assert.That(h("## F-A  Alpha\n- R-1  Show the timer in red\n"), Is.Not.EqualTo(baseline));
            Assert.That(h("## F-A  Alpha\n- R-1  Show the timer [manual]\n"), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void NeverGuesses()
        {
            var p = PlanParser.Parse("- R-1 orphan\n## F-A\n## Feature without id\n## F-B  B\n- R-1  x\n- R-1  dup\n- bad line\ndeps: contract F-A\n- R-2  y [urgent]\n");
            var e = string.Join("\n", p.Errors);
            StringAssert.Contains("line 1: requirement/deps outside a feature", e);
            StringAssert.Contains("line 3: feature heading", e);
            StringAssert.Contains("duplicate id R-1", e);
            StringAssert.Contains("line 7: requirement must be", e);
            StringAssert.Contains("contract dependency needs the interface", e);
            StringAssert.Contains("unknown requirement tag [urgent]", e);
        }
    }

    public sealed class PlanStatusTests
    {
        private static readonly DateTime Run = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

        private const string PlanText =
            "## F-CORE  Core  [P0] [modules: Core]\n- R-OK  passes\n- R-FAIL  fails\n- R-NEW  no test\n- R-WAIT  not run\n- R-LOOK  looks good [manual]\n" +
            "## F-UI  UI  [P1] [modules: Ui]\n- R-UI  ui works\ndeps: hard F-CORE\n" +
            "## F-ADS  Ads  [P1] [modules: Ads]\n- R-ADS  ads work\n" +
            "## F-RUSH  Rushed  [P1] [modules: Game] [rush]\n- R-RUSH  uses ads\ndeps: contract F-ADS (IAds)\n" +
            "## F-SLOW  Not rushed  [P2] [modules: Game2]\n- R-SLOW  uses ads too\ndeps: contract F-ADS (IAds)\n" +
            "## F-LATER  Later [P3] [deferred]\n- R-LATER  x\n";

        private static PlanInputs Inputs(string plan = PlanText)
        {
            var results = new TestResultsIndex();
            void T(string key, bool ok) => results.Add(new TestOutcome { Key = key, Passed = ok, RunUtc = Run });
            T("CoreTests.Ok", true); T("CoreTests.Fail", false); T("UiTests.Ui", true); T("AdsTests.Ads", true); T("RushTests.Rush", true);
            return new PlanInputs
            {
                Plan = PlanParser.Parse(plan),
                Covers = new List<CoverEntry>
                {
                    C("R-OK", "CoreTests", "Ok"), C("R-FAIL", "CoreTests", "Fail"), C("R-WAIT", "CoreTests", "Wait"),
                    C("R-UI", "UiTests", "Ui"), C("R-ADS", "AdsTests", "Ads"), C("R-RUSH", "RushTests", "Rush"), C("R-SLOW", "RushTests", "Rush")
                },
                Results = results,
                ModuleChangedUtc = m => Run.AddMinutes(-5),
                ContractFrozen = i => i == "IAds",
            };
        }

        private static CoverEntry C(string req, string cls, string method) => new CoverEntry { Requirement = req, Class = cls, Method = method, File = "x.cs" };

        private static string S(PlanReport r, string req) => r.Requirement(req).Status;

        [Test]
        public void RequirementStatuses_AreComputedFromEvidence()
        {
            var r = PlanStatus.Compute(Inputs());
            Assert.That(S(r, "R-OK"), Is.EqualTo(ReqStatus.Verified));
            Assert.That(S(r, "R-FAIL"), Is.EqualTo(ReqStatus.Failing));
            Assert.That(S(r, "R-NEW"), Is.EqualTo(ReqStatus.NoTest));
            Assert.That(S(r, "R-WAIT"), Is.EqualTo(ReqStatus.NotRun));
            Assert.That(S(r, "R-LOOK"), Is.EqualTo(ReqStatus.NeedsReview));
            Assert.That(S(r, "R-LATER"), Is.EqualTo(ReqStatus.Deferred));
            Assert.That(r.Feature("F-CORE").Progress, Is.EqualTo("1/5 requirement đã kiểm chứng"));
        }

        [Test]
        public void CodeChangedAfterTheRun_IsUnverified()
        {
            var i = Inputs();
            i.ModuleChangedUtc = m => m == "Core" ? Run.AddMinutes(3) : Run.AddMinutes(-5);
            var r = PlanStatus.Compute(i);
            Assert.That(S(r, "R-OK"), Is.EqualTo(ReqStatus.Unverified));
            Assert.That(S(r, "R-UI"), Is.EqualTo(ReqStatus.Unverified), "a hard dependency's code changed too");
        }

        [Test]
        public void RewordedAfterVerification_IsStale_UntilATestChanges()
        {
            var i = Inputs();
            var oldHash = i.Plan.Requirement("R-OK").Hash;
            i.Plan = PlanParser.Parse(PlanText.Replace("- R-OK  passes", "- R-OK  passes twice"));
            i.Ledger["R-OK"] = new LedgerEntry { RequirementHash = oldHash, TestsHash = "t1" };
            i.TestsHash = _ => "t1";
            Assert.That(S(PlanStatus.Compute(i), "R-OK"), Is.EqualTo(ReqStatus.Stale));
            StringAssert.Contains("no covering test was updated", PlanStatus.Compute(i).Requirement("R-OK").Detail);
            i.TestsHash = _ => "t2";
            StringAssert.Contains("tests were updated", PlanStatus.Compute(i).Requirement("R-OK").Detail);
        }

        [Test]
        public void ManualRequirement_AcceptedForThisWordingOnly()
        {
            var i = Inputs();
            i.Accepted["R-LOOK"] = i.Plan.Requirement("R-LOOK").Hash;
            Assert.That(S(PlanStatus.Compute(i), "R-LOOK"), Is.EqualTo(ReqStatus.Accepted));
            i.Plan = PlanParser.Parse(PlanText.Replace("looks good [manual]", "looks great [manual]"));
            Assert.That(S(PlanStatus.Compute(i), "R-LOOK"), Is.EqualTo(ReqStatus.Stale));
        }

        [Test]
        public void HardDependency_Blocks_RushOnlyBendsFrozenContracts_AndLeavesDebt()
        {
            var r = PlanStatus.Compute(Inputs());
            Assert.That(r.Feature("F-UI").BlockedBy, Has.Some.Contains("hard F-CORE"));
            Assert.That(r.Feature("F-UI").Status, Is.Not.EqualTo(FeatureStatus.Done), "all its requirements pass, but its hard dependency isn't done");

            // F-ADS is DONE here (its test passes) → no debt; make it not done:
            var i = Inputs(PlanText.Replace("- R-ADS  ads work", "- R-ADS  ads work\n- R-ADS-2  no fill"));
            r = PlanStatus.Compute(i);
            var rush = r.Feature("F-RUSH");
            Assert.That(rush.Debt, Has.Count.EqualTo(1));
            Assert.That(rush.BlockedBy, Is.Empty, "rush + frozen contract: allowed");
            Assert.That(rush.Status, Is.Not.EqualTo(FeatureStatus.Done), "debt keeps it from DONE even though R-RUSH passes");
            Assert.That(r.Gaps.Any(g => g.Kind == "DEBT" && g.Feature == "F-RUSH"), Is.True);
            Assert.That(r.Feature("F-SLOW").BlockedBy, Has.Some.Contains("[rush]"), "same dependency without rush: blocked");

            i.ContractFrozen = _ => false;
            Assert.That(PlanStatus.Compute(i).Feature("F-RUSH").BlockedBy, Has.Some.Contains("chưa chốt interface"), "rush never skips an unfrozen contract");
        }

        [Test]
        public void Scheduler_ParallelLanes_DoNotShareModules_AndRespectOpenTasks()
        {
            var plan = "## F-A  A [P0] [modules: M1]\n- R-A  a\n## F-B  B [P0] [modules: M2]\n- R-B  b\n## F-C  C [P1] [modules: M1]\n- R-C  c\n## F-D  D [P1] [modules: M3]\n- R-D  d\ndeps: hard F-A\n";
            var r = PlanStatus.Compute(new PlanInputs { Plan = PlanParser.Parse(plan), ContractFrozen = _ => false });
            var next = PlanScheduler.Next(r);
            Assert.That(next.Where(x => x.Lane == "now").Select(x => x.Feature), Is.EqualTo(new[] { "F-A", "F-B" }));
            Assert.That(next.Single(x => x.Feature == "F-C").Lane, Does.StartWith("after F-A"));
            Assert.That(next.Single(x => x.Feature == "F-D").Lane, Is.EqualTo("blocked"));
            StringAssert.Contains("--req R-A", next.First().Command);

            var busy = new[] { new RegisteredTask { Id = "T9", Modules = { "M2" }, Status = AgentTask.Active } };
            var withTask = PlanScheduler.Next(r, busy);
            Assert.That(withTask.Single(x => x.Feature == "F-B").Lane, Does.StartWith("after task T9"));
        }

        [Test]
        public void Gaps_IncludeUnplannedCodeAndProposals_ForTheHuman()
        {
            var i = Inputs();
            i.ChangedModules = new List<string> { "Core", "Mystery" };
            i.Proposals = new List<string> { "20261118-x.md" };
            var g = PlanStatus.Compute(i).Gaps;
            Assert.That(g.Single(x => x.Kind == "UNPLANNED_CODE").Detail, Does.Contain("Features/Mystery"));
            Assert.That(g.Single(x => x.Kind == "PROPOSAL").Actor, Is.EqualTo("human"));
            Assert.That(g.First().Kind, Is.EqualTo(ReqStatus.Failing), "most severe first");
        }

        [Test]
        public void TaskBecomesStale_WhenTheHumanChangesItsRequirements()
        {
            var plan = PlanParser.Parse(PlanText);
            var task = new AgentTask { Id = "T1", Status = AgentTask.Active };
            task.Requirements["R-OK"] = plan.Requirement("R-OK").Hash;
            task.Requirements["R-NEW"] = plan.Requirement("R-NEW").Hash;
            Assert.That(TaskPlanLink.Stale(plan, task), Is.Empty);

            var edited = PlanParser.Parse(PlanText.Replace("- R-OK  passes", "- R-OK  passes faster").Replace("- R-NEW  no test\n", ""));
            var stale = TaskPlanLink.Stale(edited, task);
            Assert.That(stale, Has.Some.Contains("R-OK was changed in the plan (now: \"passes faster\")"));
            Assert.That(stale, Has.Some.Contains("R-NEW was removed"));
        }
    }

    /// <summary>Parallel agents: two worktrees of one repository share the task registry.</summary>
    public sealed class ParallelWorktreeTests
    {
        private string _dir;

        private static void Git(string cwd, string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wt-" + Guid.NewGuid().ToString("N"));
            string main = Path.Combine(_dir, "main");
            Directory.CreateDirectory(Path.Combine(main, "Assets/_Game/Features/A"));
            Directory.CreateDirectory(Path.Combine(main, "Assets/_Game/Features/B"));
            File.WriteAllText(Path.Combine(main, "Assets/_Game/Features/A/x.txt"), "a");
            File.WriteAllText(Path.Combine(main, "Assets/_Game/Features/B/y.txt"), "b");
            Git(main, "init -q");
            Git(main, "add -A");
            Git(main, "-c user.email=a@b -c user.name=t commit -qm init");
            Git(main, "worktree add -q ../second");
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }

        [Test]
        public void SecondWorktree_CannotTakeTheSameModules_ButCanTakeOthers()
        {
            string main = Path.Combine(_dir, "main"), second = Path.Combine(_dir, "second");
            Recorder.Root = main;
            Assert.That(AgentCli.Task(main, new[] { "task", "start", "work on A", "--feature", "A" }), Is.EqualTo(0));
            Assert.That(TaskRegistry.Load(second).Single().Modules, Is.EqualTo(new[] { "A" }), "registry is shared via the git common dir");

            Recorder.Root = second;
            Assert.That(AgentCli.Task(second, new[] { "task", "start", "also A", "--feature", "A" }), Is.EqualTo(1));
            Assert.That(AgentCli.Task(second, new[] { "task", "start", "work on B", "--feature", "B" }), Is.EqualTo(0));

            // The worktree of task B disappears (removed by a human, or the agent's machine is gone): its lock is released.
            var registry = TaskRegistry.PathOf(second);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<RegisteredTask>>(File.ReadAllText(registry));
            entries.Add(new RegisteredTask { Id = "T-GONE", Worktree = Path.Combine(_dir, "deleted-worktree"), Modules = { "C" }, Status = AgentTask.Active });
            File.WriteAllText(registry, System.Text.Json.JsonSerializer.Serialize(entries));
            Assert.That(TaskRegistry.Conflicts(second, new[] { "C" }), Is.Empty, "stale lock of a vanished worktree");

            Recorder.Root = main;
            AgentCli.Task(main, new[] { "task", "close" });
            Recorder.Root = second;
            Assert.That(TaskRegistry.Conflicts(second, new[] { "A" }), Is.Empty, "closing frees the modules");
        }
    }
}

namespace LogicTests.AgentControl
{
    public sealed class HumanEditTests
    {
        [Test]
        public void AHumanEditingThePlan_IsUnobserved_NotTheAgentsScopeViolation_ButTheTaskBecomesStale()
        {
            string root = Path.Combine(Path.GetTempPath(), "human-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets/_Game/Features/A"));
            Directory.CreateDirectory(Path.Combine(root, "plan"));
            File.WriteAllText(Path.Combine(root, "Assets/_Game/Features/A/x.cs"), "class X {}\n");
            File.WriteAllText(Path.Combine(root, "plan/features.md"), "## F-A  A [P0] [modules: A]\n- R-A  old wording\n");
            void Git(string a)
            {
                var p = Process.Start(new ProcessStartInfo("git", a) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            }
            Git("init -q"); Git("add -A"); Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = root;
            try
            {
                Assert.That(AgentCli.Task(root, new[] { "task", "start", "work", "--req", "R-A" }), Is.EqualTo(0));
                File.WriteAllText(Path.Combine(root, "plan/features.md"), "## F-A  A [P0] [modules: A]\n- R-A  new wording\n"); // the human, in the IDE
                var input = HookInput.Parse("{\"session_id\":\"s\"}");
                var findings = AgentHooks.StopChecks(root, input, _ => null);

                Assert.That(findings.Any(f => f.StartsWith("SCOPE")), Is.False, string.Join("\n", findings));
                Assert.That(findings, Has.Some.Contains("STALE task").And.Contains("new wording"));
                Assert.That(EventLog.For(root).ReadAll().Any(e => e.Type == EventTypes.UnobservedChange && e.Get("path") == "plan/features.md"), Is.True);
            }
            finally
            {
                Recorder.Root = null;
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }
}
