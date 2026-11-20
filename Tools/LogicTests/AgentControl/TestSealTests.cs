using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    public sealed class TestLintTests
    {
        private static List<string> L(string body) => TestLint.Check("public void T()\n{\n" + body + "\n}", null);

        [Test]
        public void ARealAssertion_Passes_TheUsualFakesDont()
        {
            Assert.That(L("Assert.That(Coins.Reward(3, true), Is.EqualTo(11));"), Is.Empty);
            Assert.That(L("Assert.Throws<ArgumentException>(() => Coins.Reward(-1, true));"), Is.Empty);
            Assert.That(L(""), Has.Some.Contains("empty"));
            Assert.That(L("var x = Coins.Reward(3, true);"), Has.Some.Contains("no assertion"));
            Assert.That(L("Coins.Reward(3, true); Assert.Pass();"), Has.Some.Contains("Assert.Pass"));
            Assert.That(L("Assert.That(true, Is.True);"), Has.Some.Contains("literal"));
            Assert.That(L("var x = 3; Assert.That(x, Is.EqualTo(x));"), Has.Some.Contains("itself"));
            Assert.That(L("try { Assert.That(Coins.Reward(3, true), Is.EqualTo(11)); } catch (Exception) { }"), Has.Some.Contains("swallows"));
        }

        [Test]
        public void AHelperThatAsserts_Counts()
        {
            const string file = "class T { void Check(int got) { Assert.That(got, Is.EqualTo(11)); } public void A() { Check(Coins.Reward(3, true)); } }";
            Assert.That(TestLint.Check("public void A() { Check(Coins.Reward(3, true)); }", file), Is.Empty);
        }
    }

    public sealed class RedFirstTests
    {
        private static readonly List<CoverEntry> Covers = new List<CoverEntry> { new CoverEntry { Requirement = "R-A-ONE", Class = "ATests", Method = "Reward" } };
        private static List<TestRun> R(bool passed, string message = "  Expected: 11\n  But was:  0") =>
            new List<TestRun> { new TestRun { Key = "ATests.Reward", Passed = passed, Message = passed ? null : message }, new TestRun { Key = "Other.X", Passed = true } };

        [Test]
        public void OnlyAStableAssertionFailure_IsRed()
        {
            Assert.That(TestSeal.EvaluateRed(Covers, R(false), R(false)).Verdict, Is.EqualTo("red"));
            Assert.That(TestSeal.EvaluateRed(Covers, R(true), R(true)).Verdict, Is.EqualTo("green"));
            Assert.That(TestSeal.EvaluateRed(Covers, R(false), R(true)).Verdict, Is.EqualTo("flaky"));
            Assert.That(TestSeal.EvaluateRed(Covers, R(false, "System.NotImplementedException : The method"), R(false, "System.NotImplementedException : The method")).Verdict, Is.EqualTo("exceptions"));
            Assert.That(TestSeal.EvaluateRed(Covers, null, null).Verdict, Is.EqualTo("build"));
            Assert.That(TestSeal.EvaluateRed(Covers, new List<TestRun>(), new List<TestRun>()).Verdict, Is.EqualTo("pending"), "Unity-only tests: confirmed by CI");
        }
    }

    /// <summary>Seal / dispute / rule on a throw-away repo, with a fake test runner.</summary>
    public sealed class TestSealTests
    {
        private const string Plan = "# Plan: T\n\n## F-A  Feature A  [modules: A]\n- R-A-ONE  Một  [critical]\n- R-A-TWO  Hai  [critical]\n- R-A-THREE  Ba  [critical]\n";
        private const string TestFile = "UnityProject/Assets/_Game/Features/A/Tests/ATests.cs";
        private string _root;
        private List<TestRun> _runs;
        private readonly List<string> _ranIn = new List<string>();
        private Func<string, string, List<TestRun>> _savedRunner;

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

        private void Commit() { Git("-c user.email=a@b -c user.name=t add -A"); Git("-c user.email=a@b -c user.name=t commit -qm x"); }

        private static string Tests(params string[] reqs) =>
            "class ATests {\n" + string.Concat(reqs.Select(r => "    [Test, Covers(\"" + r + "\")]\n    public void " + Name(r) + "() { Assert.That(Coins.Reward(3, true), Is.EqualTo(11)); }\n")) + "}\n";

        private static string Name(string req) => "T" + req.Replace("-", "");

        private static List<TestRun> Red(params string[] reqs) => reqs.Select(r => new TestRun { Key = "ATests." + Name(r), Passed = false, Message = "  Expected: 11\n  But was:  0" }).ToList();

        private AgentTask Task(string role, string id, params string[] reqs)
        {
            var t = ScopePolicy.Start(id, "x", new[] { "A" }, new string[0], false, "now");
            t.Role = role;
            foreach (var r in reqs)
                t.Requirements[r] = PlanParser.Parse(Plan).Requirement(r).Hash;
            TaskStore.Save(_root, t);
            Recorder.Emit(Recorder.New(EventTypes.TaskCreated, "task-cli").With("id", id));
            return t;
        }

        private void WrittenBy(string session, string rel = TestFile)
        {
            Recorder.Session = session;
            Recorder.Emit(Recorder.New(EventTypes.FileModified, "hook").With("path", rel).With("via", "Edit"));
        }

        private SealState State(string req) => SealStore.Load(_root)[req];

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "seal-" + Guid.NewGuid().ToString("N"));
            Write(PlanParser.RelativePath, Plan);
            Write(Logic, "class Coins\n{\n    public static int Reward(int c, bool w) => default;\n}\n");
            Git("init -q");
            Commit();
            Recorder.Root = _root;
            _savedRunner = TestSeal.Runner;
            TestSeal.Runner = (root, filter) => { _ranIn.Add(root); return _runs; };
        }

        [TearDown]
        public void TearDown()
        {
            TestSeal.Runner = _savedRunner;
            Recorder.Root = null;
            Recorder.Session = "cli";
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        /// <summary>Session sA writes and seals tests for the given requirements.</summary>
        private void SealAs(string session, params string[] reqs)
        {
            var t = Task(TestRoles.Tests, "T-" + session + reqs.Length, reqs);
            Write(TestFile, Tests(reqs));
            WrittenBy(session);
            Commit();
            _runs = Red(reqs);
            var o = TestSeal.Seal(_root, t, reqs, session, "2026-11-20T10:00:0" + reqs.Length + "Z");
            Assert.That(o.Ok, Is.True, string.Join("\n", o.Messages));
        }

        [Test]
        public void ASeal_RecordsWhoWroteTheTests_AndThatTheyWereRed()
        {
            SealAs("sA", "R-A-ONE");
            var s = State("R-A-ONE");
            Assert.That(s.Void, Is.False);
            Assert.That(s.Author, Is.EqualTo("sA"));
            Assert.That(s.Red, Is.EqualTo("1/1"));
            Assert.That(s.Hash, Is.EqualTo(SealStore.TestsHash(_root, PlanRules.Covers(_root).Where(c => c.Requirement == "R-A-ONE"))));
            string file = Path.Combine(_root, SealStore.FileOf("F-A"));
            Assert.That(SealStore.IsGateWrite(_root, SealStore.FileOf("F-A"), WorkSnapshot.HashFile(file)), Is.True);
            File.AppendAllText(file, "seal R-A-TWO tests=x author=me red=1/1\n");
            Assert.That(SealStore.IsGateWrite(_root, SealStore.FileOf("F-A"), WorkSnapshot.HashFile(file)), Is.False, "a hand-written line is not the gate's");
        }

        [Test]
        public void ASeal_IsRefused_WhenTheTestsAreGreen_UncommittedForeignOrInTheWrongRole()
        {
            var t = Task(TestRoles.Tests, "T1", "R-A-ONE");
            Write(TestFile, Tests("R-A-ONE"));
            WrittenBy("sA");
            _runs = Red("R-A-ONE");
            Assert.That(TestSeal.Seal(_root, t, new[] { "R-A-ONE" }, "sA", "now").Messages, Has.Some.Contains("commit"));
            Commit();
            Assert.That(TestSeal.Seal(_root, t, new[] { "R-A-ONE" }, "sB", "now").Messages, Has.Some.Contains("written by"), "only the session that wrote them seals");
            _runs = Red("R-A-ONE").Select(r => { r.Passed = true; return r; }).ToList();
            Assert.That(TestSeal.Seal(_root, t, new[] { "R-A-ONE" }, "sA", "now").Messages, Has.Some.Contains("already passes"));
            t.Role = TestRoles.Impl;
            Assert.That(TestSeal.Seal(_root, t, new[] { "R-A-ONE" }, "sA", "now").Ok, Is.False);
            Assert.That(SealStore.Load(_root).ContainsKey("R-A-ONE"), Is.False);
        }

        [Test]
        public void ADispute_GoesToAnArbiter_TheThirdOneToTheOwner()
        {
            SealAs("sA", "R-A-ONE");
            var impl = Task(TestRoles.Impl, "T2", "R-A-ONE");
            Assert.That(TestSeal.Dispute(_root, impl, "R-A-ONE", "sB", "", "t").Ok, Is.False, "a dispute needs a reason");
            Assert.That(TestSeal.Dispute(_root, impl, "R-A-ONE", "sB", "R says 5+2, test wants 10+1", "t").Ok, Is.True);
            Assert.That(State("R-A-ONE").OpenDispute, Is.True);
            Assert.That(State("R-A-ONE").Ask, Is.False, "not the owner's work yet");

            var arb = Task(TestRoles.Arbiter, "T3", "R-A-ONE");
            Assert.That(TestSeal.Rule(_root, arb, "R-A-ONE", "keep", "sA", "it is right", "t").Ok, Is.False, "not the author");
            Assert.That(TestSeal.Rule(_root, arb, "R-A-ONE", "keep", "sB", "it is right", "t").Ok, Is.False, "not the disputer");
            Assert.That(TestSeal.Rule(_root, arb, "R-A-ONE", "keep", "sC", "R-A-ONE says 10+1", "t").Ok, Is.True);
            Assert.That(State("R-A-ONE").OpenDispute, Is.False);

            TaskStore.Save(_root, impl);
            TestSeal.Dispute(_root, impl, "R-A-ONE", "sB", "still wrong", "t");
            TestSeal.Rule(_root, arb, "R-A-ONE", "keep", "sC", "still right", "t");
            var third = TestSeal.Dispute(_root, impl, "R-A-ONE", "sB", "really wrong", "t");
            Assert.That(third.Messages, Has.Some.Contains("owner"));
            Assert.That(State("R-A-ONE").Ask, Is.True);
        }

        [Test]
        public void AFixRuling_Unseals_AndTheNewSealMustBeRedAgainstTheOldCode()
        {
            SealAs("sA", "R-A-ONE");
            string oldCommit = State("R-A-ONE").Commit;
            TestSeal.Dispute(_root, Task(TestRoles.Impl, "T2", "R-A-ONE"), "R-A-ONE", "sB", "expects 10+1, R says 5+2", "t");
            var arb = Task(TestRoles.Arbiter, "T3", "R-A-ONE");
            Assert.That(TestSeal.Rule(_root, arb, "R-A-ONE", "fix", "sC", "R says 5+2", "t").Ok, Is.True);
            Assert.That(State("R-A-ONE").Void, Is.True);

            Write(Logic, "class Coins\n{\n    public static int Reward(int c, bool w) => w ? 5 + 2 * c : 0;\n}\n");   // impl went on meanwhile
            Write(TestFile, Tests("R-A-ONE").Replace("Is.EqualTo(11)", "Is.EqualTo(11) /* 5 + 2*3 */"));
            WrittenBy("sC");
            Commit();
            _ranIn.Clear();
            _runs = Red("R-A-ONE");
            var o = TestSeal.Seal(_root, arb, new[] { "R-A-ONE" }, "sC", "t2");
            Assert.That(o.Ok, Is.True, string.Join("\n", o.Messages));
            Assert.That(_ranIn, Has.All.Not.EqualTo(_root), "red is checked in a worktree of the old seal's commit, not on today's code");
            Assert.That(State("R-A-ONE").Author, Is.EqualTo("sC"));
            Assert.That(State("R-A-ONE").PreviousCommit, Is.EqualTo(oldCommit));
        }

        [Test]
        public void AnAuthorWhoseSealsGetFixedTooOften_HasTheRestReChecked()
        {
            SealAs("sA", "R-A-ONE", "R-A-TWO", "R-A-THREE");
            foreach (var r in new[] { "R-A-ONE", "R-A-TWO" })
            {
                TestSeal.Dispute(_root, Task(TestRoles.Impl, "TI" + r, r), r, "sB", "wrong", "t");
                TestSeal.Rule(_root, Task(TestRoles.Arbiter, "TA" + r, r), r, "fix", "sC", "wrong", "t");
            }
            Assert.That(State("R-A-THREE").Suspect, Is.True, "2 of sA's last seals were fixed: the third is re-checked before impl hits it");
            TestSeal.Rule(_root, Task(TestRoles.Arbiter, "TA3", "R-A-THREE"), "R-A-THREE", "keep", "sC", "this one is fine", "t");
            Assert.That(State("R-A-THREE").Suspect, Is.False);
        }

        private HookInput Hook(string tool, string session, string key, string value) =>
            HookInput.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = session, ["tool_name"] = tool, ["tool_use_id"] = "u" + Guid.NewGuid().ToString("N").Substring(0, 6),
                ["tool_input"] = new Dictionary<string, string> { [key] = value }
            }));

        private const string Logic = "UnityProject/Assets/_Game/Features/A/Logic/Coins.cs";

        private static List<string> NoRules(IEnumerable<string> files) => new List<string>();

        [Test]
        public void TheSessionThatSealedTheTests_CannotWriteTheirCode_NorRuleOnThem()
        {
            SealAs("sA", "R-A-ONE");
            Task(TestRoles.Impl, "T2", "R-A-ONE");
            Assert.That(AgentHooks.Pre(_root, Hook("Edit", "sA", "file_path", Path.Combine(_root, Logic))), Is.EqualTo(2));
            Assert.That(AgentHooks.Pre(_root, Hook("Bash", "sA", "command", "echo x")), Is.EqualTo(2), "not through Bash either");
            Assert.That(AgentHooks.Pre(_root, Hook("Edit", "sB", "file_path", Path.Combine(_root, Logic))), Is.EqualTo(0));

            TestSeal.Dispute(_root, TaskStore.Load(_root), "R-A-ONE", "sB", "wrong", "t");
            Task(TestRoles.Arbiter, "T3", "R-A-ONE");
            Assert.That(AgentHooks.Pre(_root, Hook("Bash", "sA", "command", "echo x")), Is.EqualTo(2), "the author can't rule");
            Assert.That(AgentHooks.Pre(_root, Hook("Bash", "sB", "command", "echo x")), Is.EqualTo(2), "the disputer can't rule");
            Assert.That(AgentHooks.Pre(_root, Hook("Bash", "sC", "command", "echo x")), Is.EqualTo(0));
            Assert.That(TestSeal.CurrentSession(_root), Is.EqualTo("sC"), "gate commands run through Bash know their session");
        }

        [Test]
        public void TheSealCommandRunThroughBash_IsInScope_AHandEditIsNot()
        {
            var t = Task(TestRoles.Tests, "T1", "R-A-ONE");
            Write(TestFile, Tests("R-A-ONE"));
            WrittenBy("sA");
            Commit();
            _runs = Red("R-A-ONE");
            var bash = Hook("Bash", "sA", "command", "./Tools/test-gate.sh tests seal R-A-ONE");
            Assert.That(AgentHooks.Pre(_root, bash), Is.EqualTo(0));
            Assert.That(TestSeal.Seal(_root, t, new[] { "R-A-ONE" }, "sA", "t").Ok, Is.True);
            Assert.That(AgentHooks.Post(_root, bash, NoRules), Is.EqualTo(0));

            var forge = Hook("Bash", "sA", "command", "echo seal R-A-TWO >> Docs/Plan/Seals/F-A.txt");
            AgentHooks.Pre(_root, forge);
            File.AppendAllText(Path.Combine(_root, SealStore.FileOf("F-A")), "seal R-A-TWO tests=x author=sA red=1/1\n");
            Assert.That(AgentHooks.Post(_root, forge, NoRules), Is.EqualTo(2));
        }

        [Test]
        public void TheTestsRole_WritingTheSolution_IsReported()
        {
            Task(TestRoles.Tests, "T1", "R-A-ONE");
            var edit = Hook("Edit", "sA", "file_path", Path.Combine(_root, Logic));
            Assert.That(AgentHooks.Pre(_root, edit), Is.EqualTo(0), "skeleton files are in scope");
            Write(Logic, "class Coins\n{\n    public static int Reward(int c, bool w) => w ? 5 + 2 * c : 0;\n}\n");
            Assert.That(AgentHooks.Post(_root, edit, NoRules), Is.EqualTo(2));
            Write(Logic, "class Coins\n{\n    public static int Reward(int c, bool w) => default;\n    public static int Total() => default;\n}\n");
            Assert.That(AgentHooks.Post(_root, edit, NoRules), Is.EqualTo(0), "adding a skeleton member is fine");
        }

        private int Start(params string[] extra)
        {
            var t = TaskStore.Load(_root);
            if (t != null) { t.Status = AgentTask.Closed; TaskStore.Save(_root, t); }
            return AgentCli.Task(_root, new[] { "task", "start", "x" }.Concat(extra).ToArray());
        }

        [Test]
        public void TaskStart_ImplNeedsSealedTests_ArbiterNeedsADispute()
        {
            Assert.That(Start("--role", "impl", "--req", "R-A-ONE"), Is.EqualTo(1), "no sealed tests yet");
            Assert.That(Start("--role", "boss", "--req", "R-A-ONE"), Is.EqualTo(1));
            Assert.That(Start("--role", "tests", "--feature", "A"), Is.EqualTo(1), "a role needs its requirements");
            Assert.That(Start("--role", "tests", "--req", "R-A-ONE"), Is.EqualTo(0));
            Assert.That(TaskStore.Load(_root).Role, Is.EqualTo(TestRoles.Tests));

            SealAs("sA", "R-A-ONE");
            Assert.That(Start("--role", "arbiter", "--req", "R-A-ONE"), Is.EqualTo(1), "nothing to rule on");
            Assert.That(Start("--role", "impl", "--req", "R-A-ONE"), Is.EqualTo(0));
            TestSeal.Dispute(_root, TaskStore.Load(_root), "R-A-ONE", "sB", "wrong", "t");
            Assert.That(Start("--role", "impl", "--req", "R-A-ONE"), Is.EqualTo(1), "disputed: wait for the arbiter, work on something else");
            Assert.That(Start("--role", "arbiter", "--req", "R-A-ONE"), Is.EqualTo(0));
        }

        [Test]
        public void ASealedRed_IsPendingWorkForTheGate_UntilItPassedOnce_ThenRedIsARegression()
        {
            SealAs("sA", "R-A-ONE");
            Assert.That(TestSeal.AwaitingImpl(_root), Has.Member("ATests." + Name("R-A-ONE")), "red by design: the gate reports it, doesn't fail on it");
            var results = new TestGate.Planning.TestResultsIndex();
            results.Add(new TestGate.Planning.TestOutcome { Key = "ATests." + Name("R-A-ONE"), Passed = false, RunUtc = DateTime.UtcNow });
            Assert.That(TestSeal.MarkVerified(_root, PlanParser.Load(_root), results), Is.Empty, "still red: not verified");

            results = new TestGate.Planning.TestResultsIndex();
            results.Add(new TestGate.Planning.TestOutcome { Key = "ATests." + Name("R-A-ONE"), Passed = true, RunUtc = DateTime.UtcNow });
            Assert.That(TestSeal.MarkVerified(_root, PlanParser.Load(_root), results), Is.EqualTo(new[] { "R-A-ONE" }));
            Assert.That(State("R-A-ONE").Verified, Is.True);
            Assert.That(TestSeal.AwaitingImpl(_root), Is.Empty, "verified once: red again is a regression, the gate fails on it");
            Assert.That(File.ReadAllText(Path.Combine(_root, SealStore.FileOf("F-A"))), Does.Contain("verified R-A-ONE"), "tracked, so CI knows it too");
        }

        [Test]
        public void TheOwnersLastResorts_AreRefusedToAgents()
        {
            SealAs("sA", "R-A-ONE");
            Assert.That(TestSeal.Cli(_root, new[] { "tests", "unseal", "R-A-ONE", "x" }), Is.EqualTo(3));
            Assert.That(TestSeal.Cli(_root, new[] { "tests", "grandfather" }), Is.EqualTo(3));
            Assert.That(AgentHooks.ForbiddenCommand("./Tools/test-gate.sh tests unseal R-A-ONE x"), Is.Not.Null);
            Assert.That(AgentHooks.ForbiddenCommand("./Tools/test-gate.sh tests grandfather"), Is.Not.Null);
            Assert.That(TestSeal.Unseal(_root, "R-A-ONE", "owner says so", "t").Ok, Is.True);
            Assert.That(State("R-A-ONE").Void, Is.True);
        }

        [Test]
        public void Grandfather_SealsTodaysTests_AsLegacy()
        {
            Write(TestFile, Tests("R-A-ONE", "R-A-TWO"));
            Commit();
            Assert.That(TestSeal.Grandfather(_root, "t").Messages, Has.Count.EqualTo(2));
            Assert.That(State("R-A-TWO").Author, Is.EqualTo("legacy"));
            Assert.That(TestSeal.Grandfather(_root, "t").Messages, Is.Empty, "once: nothing left to grandfather");
        }
    }
}
