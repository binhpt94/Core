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
    public sealed class OwnerCommandTests
    {
        [TestCase("./Tools/test-gate.sh cr decide")]
        [TestCase("bash -c './Tools/test-gate.sh task approve'")]
        [TestCase("cd x && Tools/test-gate.sh  --quiet tests unseal R-A why")]
        [TestCase("echo y | Tools/test-gate.sh roadmap accept R-A")]
        [TestCase("dotnet run --project Tools/TestGate -- art accept MathQuiz.Slime")]
        [TestCase("dotnet Tools/TestGate/bin/Debug/net8.0/TestGate.dll tests grandfather")]
        public void RunningAnOwnerCommand_IsBlocked(string cmd) => Assert.That(AgentHooks.ForbiddenCommand(cmd), Is.Not.Null);

        [TestCase("git commit -m \"only the owner runs cr decide; task approve is theirs too\"")]
        [TestCase("grep -n \"roadmap accept\" Docs/PLANNING.md")]
        [TestCase("./Tools/test-gate.sh cr list")]
        [TestCase("./Tools/test-gate.sh task show")]
        public void MentioningOne_IsNotRunningIt(string cmd) => Assert.That(AgentHooks.ForbiddenCommand(cmd), Is.Null);
    }

    public sealed class FrozenContractTests
    {
        private const string Api = "interface Game.B.Contracts.IB\n  System.Int32 Count { get; }\ninterface Game.B.Contracts.IBX\n";

        [Test]
        public void ExactTypeName_NotAWordSomewhere()
        {
            var none = new Dictionary<string, CrEntry>();
            Assert.That(CrPlanning.Frozen(null, Api, none, "IB"), Is.True);
            Assert.That(CrPlanning.Frozen(null, "interface Game.B.Contracts.IBX\n", none, "IB"), Is.False, "IBX is not IB");
            Assert.That(CrPlanning.Frozen(null, "class Game.B.Contracts.Box\n  Game.B.Contracts.IB Inner { get; }\n", none, "IB"), Is.False, "named in a member is not declared");
            Assert.That(CrPlanning.TypeName("interface Game.B.Contracts.IRepo`1"), Is.EqualTo("IRepo"));
        }

        [Test]
        public void ACrStillChangingItsMembers_UnfreezesIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "frozen-" + Guid.NewGuid().ToString("N"));
            string apiFile = Path.Combine(root, "UnityProject/Assets/_Game/Features/B/Contracts/PublicApi.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(apiFile));
            File.WriteAllText(apiFile, Api);
            try
            {
                var crs = new Dictionary<string, CrEntry>(StringComparer.Ordinal);
                CrLog.Apply(crs, "file CR-1 from=F-A to=F-B module=B kind=breaking task=T session=s ts=2026-01-01T00:00:00Z api=" +
                                 CrLog.EncodeApi(new[] { "interface Game.B.Contracts.IB", "  System.Void Add(System.Int32 n)" }) + " reason=add");
                Assert.That(CrPlanning.Frozen(root, Api, crs, "IB"), Is.False, "open CR on IB's members");
                CrLog.Apply(crs, "approve CR-1 by=arbiter:x ts=2026-01-01T00:01:00Z reason=ok");
                Assert.That(CrPlanning.Frozen(root, Api, crs, "IB"), Is.False, "approved, lines not in place yet");
                File.WriteAllText(apiFile, Api + "  System.Void Add(System.Int32 n)\n");
                Assert.That(CrPlanning.Frozen(root, File.ReadAllText(apiFile), crs, "IB"), Is.True, "its lines are in the baseline");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }

    public sealed class ProvenanceTests
    {
        [Test]
        public void RedAtStart_GreenNow_IsUntraced_OtherChangesAreNot()
        {
            var before = new Dictionary<string, string> { ["T.A (R-A)"] = "fail", ["T.B (R-B)"] = "pass", ["T.C (R-C)"] = "fail" };
            var after = new Dictionary<string, string> { ["T.A (R-A)"] = "pass", ["T.B (R-B)"] = "pass", ["T.C (R-C)"] = "fail", ["T.D (R-D)"] = "pass" };
            Assert.That(Provenance.TurnedGreen(before, after), Is.EqualTo(new[] { "T.A (R-A)" }));
        }

        [Test]
        public void OnlyMaintenanceTasks_AreJudged()
        {
            var task = new AgentTask { Id = "T1", Status = AgentTask.Active, Features = { "B" }, CoverBaseline = new Dictionary<string, string>() };
            task.Requirements["R-B"] = "h";
            Assert.That(Provenance.Findings(Path.GetTempPath(), task), Is.Empty, "a task with a requirement may turn its tests green");
        }
    }

    public sealed class MessageTraceTests
    {
        private static AgentEvent E(string type, string session, string task, string ts, params string[] kv)
        {
            var e = new AgentEvent { Type = type, Session = session, Task = task, Ts = ts, Source = "hook" };
            for (int i = 0; i + 1 < kv.Length; i += 2)
                e.Data[kv[i]] = kv[i + 1];
            return e;
        }

        [Test]
        public void AMessageThenAnUntracedEditByAnotherSession_IsPaired()
        {
            const string bPath = "UnityProject/Assets/_Game/Features/B/Logic/Y.cs";
            var a = new List<AgentEvent>
            {
                E(EventTypes.TaskCreated, "sA", "TA", "2026-01-01T10:00:00Z", "id", "TA", "requirements", "R-A"),
                E(EventTypes.AgentMessage, "sA", "TA", "2026-01-01T10:05:00Z", "to", "b-agent", "summary", "please add Add()", "fromTask", "TA"),
            };
            var b = new List<AgentEvent>
            {
                E(EventTypes.TaskCreated, "sB", "TB", "2026-01-01T09:00:00Z", "id", "TB"),
                E(EventTypes.FileModified, "sB", "TB", "2026-01-01T10:20:00Z", "path", bPath, "inScope", "true"),
                E(EventTypes.TaskCreated, "sC", "TC", "2026-01-01T09:00:00Z", "id", "TC", "requirements", "CR-1"),
                E(EventTypes.FileModified, "sC", "TC", "2026-01-01T10:30:00Z", "path", bPath, "inScope", "true"),
                E(EventTypes.FileModified, "sD", "TD", "2026-01-01T15:00:00Z", "path", bPath, "inScope", "true"),
            };
            var pairs = MessageTrace.Pairs(new[] { a, b });
            Assert.That(pairs.Select(p => p.Edit.Session), Is.EqualTo(new[] { "sB" }), "sC works from a CR; sD is hours later");
            Assert.That(pairs[0].Text, Does.Contain("b-agent").And.Contain(bPath).And.Contain("TB"));
        }
    }

    /// <summary>Two worktrees of one repository: CRs are visible across them before any merge, and adopted by the branch that does them.</summary>
    public sealed class CrAcrossWorktreesTests
    {
        private string _dir, _main, _second;

        private static void Git(string cwd, string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }

        private static void Write(string root, string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel)));
            File.WriteAllText(Path.Combine(root, rel), text);
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "crwt-" + Guid.NewGuid().ToString("N"));
            _main = Path.Combine(_dir, "main");
            _second = Path.Combine(_dir, "second");
            Write(_main, "Docs/Plan/features.md", "# Plan: T\n## F-A  Alpha [P0] [modules: A]\n- R-A  A works\n## F-B  Beta [P2] [modules: B]\n- R-B  B works\n");
            Write(_main, "UnityProject/Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Write(_main, "UnityProject/Assets/_Game/Features/B/Logic/Y.cs", "class Y {}\n");
            Write(_main, "UnityProject/Assets/_Game/Features/B/Contracts/PublicApi.txt", "interface Game.B.Contracts.IB\n  System.Int32 Count { get; }\n");
            Git(_main, "init -q");
            Git(_main, "-c user.email=a@b -c user.name=t add -A");
            Git(_main, "-c user.email=a@b -c user.name=t commit -qm init");
            Git(_main, "worktree add -q ../second");
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }

        private void As(string root, string session)
        {
            Recorder.Root = root;
            Recorder.Session = session;
            Write(root, ".agent/" + TestSeal.SessionFile, session);
        }

        [Test]
        public void ACrFiledInOneWorktree_IsVisibleInTheOther_AndAdoptedByTheTaskThatDoesIt()
        {
            As(_main, "sA");
            Assert.That(AgentCli.Task(_main, new[] { "task", "start", "A work", "--req", "R-A" }), Is.EqualTo(0));
            Assert.That(AgentCli.Task(_main, new[] { "task", "need", "B", "an INew", "--api", "interface Game.B.Contracts.INew" }), Is.EqualTo(0));

            As(_second, "sB");
            Assert.That(CrLog.Load(_second).ContainsKey("CR-1"), Is.True, "shared mirror in the git common dir");
            Assert.That(PlanParser.Load(_second).Requirement("CR-1"), Is.Not.Null, "B's worktree sees CR-1 as its requirement before any merge");
            Assert.That(CrLog.TrackedLines(_second, "F-B"), Is.Empty);

            Assert.That(AgentCli.Task(_second, new[] { "task", "start", "B does CR-1", "--from", "CR-1" }), Is.EqualTo(0));
            Assert.That(CrLog.TrackedLines(_second, "F-B"), Has.Count.EqualTo(2), "file + approve now travel with B's branch");
            var state = WorkSnapshot.HashFile(Path.Combine(_second, CrLog.FileOf("F-B")));
            Assert.That(CrStore.IsGateWrite(_second, CrLog.FileOf("F-B"), state), Is.True, "the adoption is a gate write, not a hand edit");
            Assert.That(CrLog.Load(_second)["CR-1"].History, Has.Count.EqualTo(2), "the same line in both logs is one event");
        }

        [Test]
        public void ABreakingCrApprovedLater_GrantsTheAskersOpenTask()
        {
            As(_main, "sA");
            AgentCli.Task(_main, new[] { "task", "start", "A work", "--req", "R-A" });
            AgentCli.Task(_main, new[] { "task", "need", "B", "IB must Add", "--api", "interface Game.B.Contracts.IB", "--api", "  System.Void Add(System.Int32 n)" });
            Assert.That(TaskStore.Load(_main).CrGrants, Is.Empty, "breaking: nothing granted before the arbiter rules");

            As(_second, "sArbiter");
            Assert.That(CrCli.Cli(_second, new[] { "cr", "rule", "CR-1", "keep", "one fake to update" }), Is.EqualTo(0));
            Assert.That(TaskStore.Load(_main).CrGrants.ContainsKey("CR-1"), Is.True, "the asker's task, open in the other worktree, may now write B's Contracts");

            As(_main, "sA");
            AgentCli.Task(_main, new[] { "task", "close" });
            AgentCli.Task(_main, new[] { "task", "start", "A again", "--req", "R-A" });
            Assert.That(TaskStore.Load(_main).CrGrants.ContainsKey("CR-1"), Is.True, "a later task of the asking feature gets it while the lines are not in place");
        }
    }
}
