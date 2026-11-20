using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>[dropped] requirements: they stop counting, `roadmap next` asks to retire their leftovers first.</summary>
    public sealed class RetireStatusTests
    {
        private const string Plan = "## F-A  A  [P0] [modules: M]\n- R-1  one\n- R-2  two  [dropped]\n## F-X  X  [P1] [modules: X] [dropped]\n- R-X  gone\n";
        private static readonly DateTime Run = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        private static PlanInputs Inputs(params RetireLeftover[] left)
        {
            var results = new TestResultsIndex();
            results.Add(new TestOutcome { Key = "ATests.One", Passed = true, RunUtc = Run });
            return new PlanInputs
            {
                Plan = PlanParser.Parse(Plan), Results = results, ModuleChangedUtc = _ => Run.AddMinutes(-5),
                Covers = new List<CoverEntry> { new CoverEntry { Requirement = "R-1", Class = "ATests", Method = "One", File = "a.cs" } },
                RetireLeftovers = left.ToList()
            };
        }

        [Test]
        public void TheDroppedTag_IsParsed_ForRequirementsAndViaTheirFeature()
        {
            var plan = PlanParser.Parse(Plan);
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(plan.Requirement("R-2").IsDropped, Is.True);
            Assert.That(plan.DroppedRequirements.Select(r => r.Id), Is.EquivalentTo(new[] { "R-2", "R-X" }));
        }

        [Test]
        public void ADroppedRequirement_NeitherCountsNorLeavesAGap_SoTheFeatureCanBeDone()
        {
            var report = PlanStatus.Compute(Inputs());
            Assert.That(report.Requirement("R-2").Status, Is.EqualTo(ReqStatus.Dropped));
            Assert.That(report.Feature("F-A").Total, Is.EqualTo(1));
            Assert.That(report.Feature("F-A").Status, Is.EqualTo(FeatureStatus.Done));
            Assert.That(report.Gaps.Where(g => g.Requirement == "R-2"), Is.Empty, "nothing left in code: no work");
        }

        [Test]
        public void Leftovers_BecomeRetireGaps_EvenInADroppedOrDoneFeature()
        {
            var report = PlanStatus.Compute(Inputs(
                new RetireLeftover { Requirement = "R-2", Path = "t.cs", Line = 3, Kind = "covers" },
                new RetireLeftover { Requirement = "R-X", Path = "x.cs", Line = 9, Kind = "mention" }));
            var retire = report.Gaps.Where(g => g.Kind == "RETIRE").ToList();
            Assert.That(retire.Select(g => g.Requirement), Is.EquivalentTo(new[] { "R-2", "R-X" }));
            Assert.That(retire.All(g => g.Actor == RetireScan.Actor), Is.True);
            Assert.That(retire[0].Detail, Does.Contain("t.cs:3").Or.Contain("x.cs:9"));
            Assert.That(PlanScheduler.Next(report).SelectMany(i => i.Requirements), Has.None.EqualTo("R-2"), "retire is its own lane, not a --req task");
        }

        [Test]
        public void TheScan_FindsCoversAndMentions_ButNotLongerIds()
        {
            var code = "class T {\n  [Test, Covers(\"R-2\")] public void A() { }\n  // was R-2\n  // R-2-B is another one\n}\n";
            var left = RetireScan.In("t.cs", code, new[] { "R-2" }).ToList();
            Assert.That(left.Select(l => l.Line + ":" + l.Kind), Is.EqualTo(new[] { "2:covers", "3:mention" }));
        }

        [Test]
        public void TheGuard_LetsTestsOfRetiredRequirementsGo_AndStillCatchesOthers()
        {
            const string path = "UnityProject/Assets/_Game/Features/A/Tests/ATests.cs";
            const string before = "class ATests {\n" +
                                  "    [Test]\n    [Covers(\"R-2\")]\n    public void Old() { Assert.That(1, Is.EqualTo(1)); }\n" +
                                  "    [Test, Covers(\"R-1\")]\n    public void Keep() { Assert.That(2, Is.EqualTo(2)); }\n}\n";
            const string withoutOld = "class ATests {\n    [Test, Covers(\"R-1\")]\n    public void Keep() { Assert.That(2, Is.EqualTo(2)); }\n}\n";
            const string withoutBoth = "class ATests {\n}\n";
            var retired = new[] { "R-2" };
            Assert.That(TestGuard.Check(path, before, withoutOld, retired), Is.Empty);
            Assert.That(TestGuard.Check(path, before, withoutOld), Is.Not.Empty, "without --retire it is weakening");
            Assert.That(TestGuard.Check(path, before, withoutBoth, retired), Has.Some.Contains("tests 1 -> 0"));
            Assert.That(TestGuard.Check(path, before, null, retired), Has.Some.Contains("test file deleted (1 tests"));
            Assert.That(TestGuard.Check(path, before.Replace("R-1", "R-2"), null, retired), Is.Empty, "every test covered only retired ids");
        }
    }

    /// <summary>`task start --retire` on a throw-away repo: only dropped ids, and Stop holds until the leftovers are gone.</summary>
    public sealed class RetireCliTests
    {
        private const string Plan = "# Plan: T\n\n## F-A  Feature A  [modules: A]\n- R-A-KEEP  Giữ\n- R-A-OLD  Bỏ  [dropped]\n";
        private const string TestFile = "UnityProject/Assets/_Game/Features/A/Tests/ATests.cs";
        private const string Original = "class ATests {\n    [Test, Covers(\"R-A-OLD\")]\n    public void Old() { Assert.That(1, Is.EqualTo(1)); }\n" +
                                        "    [Test, Covers(\"R-A-KEEP\")]\n    public void Keep() { Assert.That(2, Is.EqualTo(2)); }\n}\n";
        private string _root;

        private void Git(string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private static HookInput H(string tool, Dictionary<string, string> input) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sessA", ["tool_name"] = tool, ["tool_use_id"] = "tu1", ["tool_input"] = input
            }));

        private List<string> Stop() => AgentHooks.StopChecks(_root, H(null, new Dictionary<string, string>()), p => p == TestFile ? Original : null);

        private void Edit(string text)
        {
            Write(TestFile, text);
            AgentHooks.Post(_root, H("Edit", new Dictionary<string, string> { ["file_path"] = Path.Combine(_root, TestFile) }), _ => new List<string>());
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "retire-" + Guid.NewGuid().ToString("N"));
            Write(PlanParser.RelativePath, Plan);
            Write(TestFile, Original);
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm x");
            Recorder.Root = _root;
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            Recorder.Session = "cli";
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        [Test]
        public void Retire_TakesOnlyDroppedRequirements_AndScopesToTheirFeature()
        {
            Assert.That(AgentCli.Task(_root, new[] { "task", "start", "x", "--retire", "R-A-KEEP" }), Is.EqualTo(1), "only the owner cancels");
            Assert.That(AgentCli.Task(_root, new[] { "task", "start", "x", "--retire", "R-NOPE" }), Is.EqualTo(1));
            Assert.That(AgentCli.Task(_root, new[] { "task", "start", "Retire R-A-OLD", "--retire", "R-A-OLD" }), Is.EqualTo(0));
            var task = TaskStore.Load(_root);
            Assert.That(task.Retire, Is.EqualTo(new[] { "R-A-OLD" }));
            Assert.That(task.Features, Is.EqualTo(new[] { "A" }));
            Assert.That(task.Provenance, Is.EqualTo("retire"));
        }

        [Test]
        public void Stop_HoldsUntilTheDroppedRequirementIsGone_AndDeletingItsTestIsNotWeakening()
        {
            AgentCli.Task(_root, new[] { "task", "start", "Retire R-A-OLD", "--retire", "R-A-OLD" });
            Assert.That(Stop(), Has.Some.StartsWith("RETIRE_LEFT R-A-OLD " + TestFile + ":2"));

            Edit("class ATests {\n    [Test, Covers(\"R-A-KEEP\")]\n    public void Keep() { Assert.That(2, Is.EqualTo(2)); }\n}\n");
            Assert.That(Stop(), Is.Empty);

            Edit("class ATests {\n}\n");
            Assert.That(Stop(), Has.Some.StartsWith("TESTS ").And.Contains("tests 1 -> 0"), "a live requirement's test is still guarded");
        }
    }
}
