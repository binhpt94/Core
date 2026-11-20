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
    /// <summary>
    /// Separation of duties only for [critical] requirements: the others are written and implemented in one task (no --role) and
    /// count when their tests pass; old seals, disputes and re-checks of them are ignored, so nothing waits for a lane nobody has.
    /// </summary>
    public sealed class CriticalOnlyStatusTests
    {
        private static readonly DateTime Run = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        private const string Plan = "## F-A  A  [P0] [modules: M]\n- R-1  one\n- R-2  two\n- R-3  three  [critical]\n";

        private static PlanInputs Inputs(bool r1Passes = true)
        {
            var results = new TestResultsIndex();
            results.Add(new TestOutcome { Key = "ATests.One", Passed = r1Passes, RunUtc = Run });
            return new PlanInputs
            {
                Plan = PlanParser.Parse(Plan), Results = results, ModuleChangedUtc = _ => Run.AddMinutes(-5),
                Covers = new List<CoverEntry> { new CoverEntry { Requirement = "R-1", Class = "ATests", Method = "One", File = "a.cs" } },
                SealHash = _ => "h1"
            };
        }

        [Test]
        public void TheCriticalTag_IsParsed_AndLeavesTheHashAlone()
        {
            var plain = PlanParser.Parse("## F-A  A  [modules: M]\n- R-1  one  [story]\n");
            var tagged = PlanParser.Parse("## F-A  A  [modules: M]\n- R-1  one  [story]  [critical]\n");
            Assert.That(tagged.Errors, Is.Empty);
            Assert.That(tagged.Requirement("R-1").IsCritical, Is.True);
            Assert.That(plain.Requirement("R-1").IsCritical, Is.False);
            Assert.That(tagged.Requirement("R-1").Hash, Is.EqualTo(plain.Requirement("R-1").Hash), "tagging changes the process, not the behaviour: no STALE, seals stay valid");
            Assert.That(PlanParser.Parse("## F-A  A  [modules: M]\n- R-1  one  [risky]\n").Errors, Has.Some.Contains("unknown requirement tag [risky]"));
        }

        [Test]
        public void APlainRequirement_IsVerifiedByPassingTests_WithoutASeal_AndItsOldSealsAreIgnored()
        {
            var i = Inputs();
            Assert.That(PlanStatus.Compute(i).Requirement("R-1").Status, Is.EqualTo(ReqStatus.Verified));

            i.Seals["R-1"] = new SealState { Requirement = "R-1", Feature = "F-A", Hash = "old", Author = "sA", Red = "1/1", OpenDispute = true, Suspect = true };
            var report = PlanStatus.Compute(i);
            Assert.That(report.Requirement("R-1").Status, Is.EqualTo(ReqStatus.Verified), "an old dispute or tampered seal of a plain requirement blocks nothing");
            Assert.That(report.Gaps.Where(g => g.Requirement == "R-1"), Is.Empty, "no orphan gap: no arbiter lane exists for it");

            var red = PlanStatus.Compute(Inputs(r1Passes: false)).Requirement("R-1");
            Assert.That(red.Status, Is.EqualTo(ReqStatus.Failing));
            Assert.That(red.Role, Is.Null);
            Assert.That(PlanStatus.Compute(i).Requirement("R-2").Role, Is.Null, "no test yet: still one task, no --role tests");
        }

        [Test]
        public void ACriticalRequirement_StillNeedsAnIndependentSeal()
        {
            var i = Inputs();
            i.Covers.Add(new CoverEntry { Requirement = "R-3", Class = "ATests", Method = "One", File = "a.cs" });
            var r3 = PlanStatus.Compute(i).Requirement("R-3");
            Assert.That(r3.Status, Is.EqualTo(ReqStatus.Unsealed));
            Assert.That(r3.Role, Is.EqualTo(TestRoles.Tests));
            Assert.That(PlanStatus.Compute(Inputs()).Requirement("R-3").Role, Is.EqualTo(TestRoles.Tests), "no test yet: a tests session writes them");
        }

        [Test]
        public void Lanes_PlainRequirementsGetOneTaskWithoutARole_CriticalOnesKeepTheirRoles()
        {
            var next = PlanScheduler.Next(PlanStatus.Compute(Inputs(r1Passes: false)));
            var plain = next.Single(x => x.Role == null);
            Assert.That(plain.Requirements, Is.EquivalentTo(new[] { "R-1", "R-2" }));
            Assert.That(plain.Command, Does.Not.Contain("--role"));
            Assert.That(next.Single(x => x.Role == TestRoles.Tests).Command, Does.Contain("--role tests --req R-3"));
        }

        [Test]
        public void ARequirementThatBecomesCritical_MakesARolelessTaskStale_ATestsTaskIsFine()
        {
            var before = PlanParser.Parse(Plan);
            var after = PlanParser.Parse(Plan.Replace("- R-1  one\n", "- R-1  one  [critical]\n"));
            var task = new AgentTask { Id = "T1", Requirements = { ["R-1"] = before.Requirement("R-1").Hash } };
            Assert.That(TaskPlanLink.Stale(before, task), Is.Empty);
            Assert.That(TaskPlanLink.Stale(after, task), Has.Some.Contains("R-1 is [critical] now"));
            task.Role = TestRoles.Tests;
            Assert.That(TaskPlanLink.Stale(after, task), Is.Empty);
        }
    }

    /// <summary>Seal, dispute, rule and --role on a throw-away repo: refused for plain requirements, with the way forward.</summary>
    public sealed class CriticalOnlyCliTests
    {
        private const string Plan = "# Plan: T\n\n## F-A  Feature A  [modules: A]\n- R-A-PLAIN  Thường\n- R-A-HOT  Tiền  [critical]\n";
        private const string TestFile = "UnityProject/Assets/_Game/Features/A/Tests/ATests.cs";
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

        private int Start(params string[] extra)
        {
            var t = TaskStore.Load(_root);
            if (t != null) { t.Status = AgentTask.Closed; TaskStore.Save(_root, t); }
            return AgentCli.Task(_root, new[] { "task", "start", "x" }.Concat(extra).ToArray());
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "critical-" + Guid.NewGuid().ToString("N"));
            Write(PlanParser.RelativePath, Plan);
            Write("UnityProject/Assets/_Game/Features/A/Logic/Coins.cs", "class Coins { }\n");
            Write(TestFile, "class ATests {\n    [Test, Covers(\"R-A-PLAIN\")]\n    public void ThreeCorrect_Gives15Coins() { Assert.That(Coins.Reward(3), Is.EqualTo(15)); }\n}\n");
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
        public void Roles_AreOnlyForCriticalRequirements_AndCriticalOnesNeedARole()
        {
            Assert.That(Start("--role", "tests", "--req", "R-A-PLAIN"), Is.EqualTo(1), "plain: no --role");
            Assert.That(Start("--req", "R-A-PLAIN"), Is.EqualTo(0), "plain: one task writes tests and code");
            Assert.That(TaskStore.Load(_root).Role, Is.Null);
            Assert.That(Start("--req", "R-A-HOT"), Is.EqualTo(1), "critical: tests and code from separate sessions");
            Assert.That(Start("--role", "tests", "--req", "R-A-HOT"), Is.EqualTo(0));
        }

        [Test]
        public void SealDisputeAndRule_RefuseAPlainRequirement()
        {
            Assert.That(Start("--role", "tests", "--req", "R-A-HOT"), Is.EqualTo(0));
            var task = TaskStore.Load(_root);
            task.Requirements["R-A-PLAIN"] = PlanParser.Parse(Plan).Requirement("R-A-PLAIN").Hash;   // as if started before the change
            var seal = TestSeal.Seal(_root, task, new[] { "R-A-PLAIN" }, "sA", "now");
            Assert.That(seal.Ok, Is.False);
            Assert.That(seal.Messages, Has.Some.Contains("not [critical]"));
            task.Role = TestRoles.Impl;
            Assert.That(TestSeal.Dispute(_root, task, "R-A-PLAIN", "sB", "wrong", "now").Messages, Has.Some.Contains("not [critical]"));
            task.Role = TestRoles.Arbiter;
            Assert.That(TestSeal.Rule(_root, task, "R-A-PLAIN", "keep", "sC", "why", "now").Messages, Has.Some.Contains("not [critical]"));
            Assert.That(SealStore.Load(_root).ContainsKey("R-A-PLAIN"), Is.False);
        }

        [Test]
        public void TheTestSummary_SaysWhatEachTestChecks_AndWhatTheTaskChanged()
        {
            var all = TestSummary.Format(TestSummary.Build(_root, new[] { "R-A-PLAIN" }, null), onlyChanged: false);
            Assert.That(all, Is.EqualTo(new[] { "R-A-PLAIN: 1 test(s), 1 assert(s)", "  Three correct → gives 15 coins: Coins.Reward(3) == 15" }));

            Assert.That(TestSummary.Format(TestSummary.Build(_root, new[] { "R-A-PLAIN" }, f => File.ReadAllText(Path.Combine(_root, f))), onlyChanged: true),
                Is.Empty, "unchanged since the base: nothing to show at task close");
            var changed = TestSummary.Format(TestSummary.Build(_root, new[] { "R-A-PLAIN" }, f => "class ATests { public void ThreeCorrect_Gives15Coins() { Assert.That(1, Is.EqualTo(2)); } }"), onlyChanged: true);
            Assert.That(changed.First(), Is.EqualTo("R-A-PLAIN: 1 test(s) (1 changed), 1 assert(s)"));
            var added = TestSummary.Format(TestSummary.Build(_root, new[] { "R-A-PLAIN" }, f => null), onlyChanged: true);
            Assert.That(added.Last(), Does.StartWith("  [new] Three correct"));
        }
    }

    public sealed class TestSummaryTextTests
    {
        [TestCase("ThreeCorrect_Gives15Coins", "Three correct → gives 15 coins")]
        [TestCase("Timeout_AtDeadlineTick_CountsAsWrong", "Timeout → at deadline tick → counts as wrong")]
        [TestCase("SameSeed_SameQuestions", "Same seed → same questions")]
        [TestCase("UI_HUDShowsTimer", "UI → HUD shows timer")]
        public void MethodNames_ReadAsWords(string method, string expected) => Assert.That(TestSummary.Humanize(method), Is.EqualTo(expected));

        [TestCase("Assert.That(coins, Is.EqualTo(15));", "coins == 15")]
        [TestCase("Assert.That(options.Distinct().Count(), Is.EqualTo(3), \"three, all different\");", "options.Distinct().Count() == 3")]
        [TestCase("Assert.That(r.Passed, Is.False);", "!(r.Passed)")]
        [TestCase("Assert.That(x, Is.GreaterThanOrEqualTo(0));", "x >= 0")]
        [TestCase("Assert.That(list, Has.Count.EqualTo(10));", "list.Count == 10")]
        [TestCase("Assert.That(text, Does.Contain(\"Kỷ lục, mới\"));", "text contains \"Kỷ lục, mới\"")]
        [TestCase("Assert.AreEqual(5, coins);", "coins == 5")]
        [TestCase("Assert.Throws<ArgumentException>(() => Make(-1));", "throws ArgumentException")]
        [TestCase("Assert.That(t, Is.EqualTo(2.5).Within(0.1));", "t == 2.5 Within(0.1)")]
        public void Assertions_AreShortened(string code, string expected) => Assert.That(TestSummary.Assertions(code), Is.EqualTo(new[] { expected }));

        [Test]
        public void EscapesAreNotAssertions() =>
            Assert.That(TestSummary.Assertions("Assert.Pass(); Assert.That(a, Is.Null);"), Is.EqualTo(new[] { "a is null" }));
    }
}
