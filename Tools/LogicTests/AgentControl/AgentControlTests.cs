using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TestGate.AgentControl;
using NUnit.Framework;

namespace LogicTests.AgentControl
{
    public sealed class EventLogTests
    {
        private string _dir;
        private EventLog _log;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "evlog-" + Guid.NewGuid().ToString("N"));
            _log = new EventLog(Path.Combine(_dir, "events.jsonl"));
            for (int i = 0; i < 4; i++)
                _log.Append(new AgentEvent { Type = EventTypes.ToolUsed, Source = "hook", Session = "s1", Agent = "main" }.With("tool", "Read").With("n", i.ToString()));
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, true);

        private List<string> Lines() => File.ReadAllLines(_log.Path).ToList();

        [Test]
        public void Appended_IsAChain()
        {
            var all = _log.ReadAll();
            Assert.That(all.Select(e => e.Seq), Is.EqualTo(new long[] { 1, 2, 3, 4 }));
            Assert.That(all[0].Prev, Is.EqualTo(EventLog.Genesis));
            Assert.That(all[2].Prev, Is.EqualTo(all[1].Hash));
            Assert.That(_log.Verify().Ok, Is.True);
        }

        [Test]
        public void EditingALine_IsDetected_AtThatEvent()
        {
            var lines = Lines();
            lines[1] = lines[1].Replace("\"n\":\"1\"", "\"n\":\"9\"");
            var v = EventLog.Verify(lines);
            Assert.That(v.Ok, Is.False);
            Assert.That(v.BrokenAt, Is.EqualTo(2));
            StringAssert.Contains("edited", v.Message);
        }

        [Test]
        public void EditingAndRehashingALine_BreaksTheNextLink()
        {
            var lines = Lines();
            var e = EventLog.Parse(lines[1]);
            e.Data["n"] = "9";
            e.Hash = EventLog.ComputeHash(e); // a careful forger
            lines[1] = EventLog.Serialize(e);
            var v = EventLog.Verify(lines);
            Assert.That(v.BrokenAt, Is.EqualTo(3), v.Message);
        }

        [Test]
        public void DeletingOrReordering_IsDetected()
        {
            var lines = Lines();
            var deleted = lines.Where((_, i) => i != 1).ToList();
            Assert.That(EventLog.Verify(deleted).BrokenAt, Is.EqualTo(2));
            var swapped = new List<string> { lines[0], lines[2], lines[1], lines[3] };
            Assert.That(EventLog.Verify(swapped).Ok, Is.False);
        }

        [Test]
        public void TheCheckpoint_VerifiesOnlyNewEvents_ButAnOldEditIsStillFound()
        {
            Assert.That(_log.Verify().Ok, Is.True);                 // writes the checkpoint
            _log.Append(new AgentEvent { Type = EventTypes.ToolUsed, Source = "hook", Session = "s1" }.With("n", "4"));
            Assert.That(_log.Verify().Count, Is.EqualTo(5));

            var lines = Lines();
            lines[1] = lines[1].Replace("\"n\":\"1\"", "\"n\":\"9\"");
            File.WriteAllLines(_log.Path, lines);
            var v = _log.Verify();
            Assert.That(v.Ok, Is.False);
            Assert.That(v.BrokenAt, Is.EqualTo(2), "the prefix digest changed → full verification → exact place");
        }

        [Test]
        public void TheFastReader_ReadsExactlyWhatTheStrictParserReads()
        {
            _log.Append(new AgentEvent { Type = EventTypes.Claim, Source = "hook", Session = "s\"2", Agent = "x", Task = "T1" }.With("text", "dòng 1\nđã xong ✓ \"ok\""));
            foreach (var line in Lines())
                Assert.That(EventLog.Serialize(EventLog.ParseUtf8(System.Text.Encoding.UTF8.GetBytes(line))), Is.EqualTo(EventLog.Serialize(EventLog.Parse(line))));
            Assert.That(_log.ReadAll().Last().Get("text"), Is.EqualTo("dòng 1\nđã xong ✓ \"ok\""));
        }

        [Test]
        public void AfterACheckpoint_TruncatingTheTail_IsDetected()
        {
            Assert.That(_log.Verify().Ok, Is.True);
            File.WriteAllLines(_log.Path, Lines().Take(2));
            var v = _log.Verify();
            Assert.That(v.Ok, Is.False);
            StringAssert.Contains("SHORTER", v.Message);
        }

        [Test]
        public void TruncatingTheTail_IsNotDetectedLocally_HeadHashMustBeAnchored()
        {
            // Honest limit, written as a test: without a checkpoint (deleted together with the tail by someone with write
            // access to .agent/), dropping the LAST events keeps a valid chain. Anchoring the head hash (CI) closes it.
            var lines = Lines().Take(2).ToList();
            var v = EventLog.Verify(lines);
            Assert.That(v.Ok, Is.True);
            Assert.That(v.Head, Is.Not.EqualTo(_log.Verify().Head));
        }
    }

    public sealed class ScopePolicyTests
    {
        private static AgentTask Task(params string[] features) =>
            ScopePolicy.Start("T1", "fix popup", features, new string[0], false, "now");

        [Test]
        public void OneFeature_IsSelfService_MoreNeedsAHuman()
        {
            Assert.That(Task("MathQuiz").Status, Is.EqualTo(AgentTask.Active));
            Assert.That(Task("MathQuiz", "Progress").Status, Is.EqualTo(AgentTask.PendingApproval));
            Assert.That(ScopePolicy.Start("T2", "x", new[] { "MathQuiz" }, new[] { "Assets/_Game/Bootstrap" }, false, "now").Status, Is.EqualTo(AgentTask.PendingApproval));
            Assert.That(ScopePolicy.Start("T3", "x", new[] { "MathQuiz" }, new string[0], true, "now").Status, Is.EqualTo(AgentTask.PendingApproval));
        }

        [TestCase("Assets/_Game/Features/MathQuiz/Unity/Views/QuizView/QuizViewLayout.cs", true)]
        [TestCase("Assets/_Game/Features/MathQuiz/Logic/UseCases/QuizTexts.cs", true)]
        [TestCase("docs/architecture/overview.md", true)]                       // generated, verified by ARCH090
        [TestCase("Assets/_Game/Features/Progress/Logic/BestScoreService.cs", false)]
        [TestCase("Assets/_Game/Bootstrap/GameCompositionRoot.cs", false)]
        [TestCase("Assets/_Game/Features/MathQuiz/Tests/Replays/perfect.replay.txt", false)] // protected even in scope
        [TestCase("Assets/_Game/Features/MathQuiz/Tests/Layout/QuizView.layout.txt", false)]
        [TestCase(".agent/task.json", false)]
        [TestCase("tools/TestGate/Program.cs", false)]
        [TestCase("Assets/_Game/Features/MathQuizExtra/X.cs", false)]           // prefix of another feature name
        public void Check(string path, bool allowed) =>
            Assert.That(ScopePolicy.Check(Task("MathQuiz"), path).Allowed, Is.EqualTo(allowed), ScopePolicy.Check(Task("MathQuiz"), path).Reason);

        [Test]
        public void NoTask_PendingTask_DenyEverythingInTheRepo_ButNotOutside()
        {
            Assert.That(ScopePolicy.Check(null, "Assets/_Game/Features/MathQuiz/X.cs").Reason, Does.Contain("task start"));
            Assert.That(ScopePolicy.Check(Task("A", "B"), "Assets/_Game/Features/A/X.cs").Reason, Does.Contain("approval"));
            Assert.That(ScopePolicy.Check(null, null).Allowed, Is.True, "outside the repository (scratch files)");
        }

        [Test]
        public void Request_KeepsCurrentScope_ApproveMergesIt()
        {
            var t = Task("MathQuiz");
            ScopePolicy.Request(t, new[] { "Progress" }, new string[0], true, "best score bug");
            Assert.That(t.Status, Is.EqualTo(AgentTask.Active));
            Assert.That(ScopePolicy.Check(t, "Assets/_Game/Features/Progress/Logic/X.cs").Allowed, Is.False);
            ScopePolicy.Approve(t, "later");
            Assert.That(ScopePolicy.Check(t, "Assets/_Game/Features/Progress/Logic/X.cs").Allowed, Is.True);
            Assert.That(t.AllowTestChanges, Is.True);
            Assert.That(t.HasPendingRequest, Is.False);
        }

        [Test]
        public void CoreMaintenance_OpensOwnerOnlyFiles_OnlyAfterApproval_NeverTheGateLog()
        {
            var t = Task("MathQuiz");
            ScopePolicy.Request(t, new string[0], new string[0], false, "restructure the core", coreMaintenance: true);
            Assert.That(t.HasPendingRequest, Is.True);
            Assert.That(ScopePolicy.Check(t, "tools/TestGate/Program.cs").Allowed, Is.False, "requested is not approved");
            ScopePolicy.Approve(t, "later");
            Assert.That(t.ScopeText, Does.Contain("core maintenance"));
            foreach (var p in new[] { "tools/TestGate/Program.cs", ".claude/settings.json", "Assets/_Game/Architecture/Rules/FileRules.cs",
                                      "Assets/_Game/Core/Runtime/PublicApi.txt", "Assets/_Game/Bootstrap/Editor/X.cs.meta", "Assets/_Game/Features/Progress/Logic/X.cs" })
                Assert.That(ScopePolicy.Check(t, p).Allowed, Is.True, p);
            Assert.That(ScopePolicy.Check(t, ScopePolicy.GateLog + "task.json").Allowed, Is.False, "the gate's own log stays owner-only");
            Assert.That(t.AllowTestChanges, Is.False, "core maintenance does not allow weakening tests");
            t.Status = AgentTask.Closed;
            Assert.That(ScopePolicy.Check(t, "tools/TestGate/Program.cs").Allowed, Is.False, "a closed task opens nothing");
        }

        [TestCase("cat .agent/events.jsonl > /dev/null")]
        [TestCase("echo '{}' > .agent/task.json")]
        [TestCase("./tools/test-gate.sh task approve")]
        [TestCase("yes T1 | tools/test-gate.sh task approve")]
        public void ForbiddenCommands(string cmd) => Assert.That(AgentHooks.ForbiddenCommand(cmd), Is.Not.Null);

        [TestCase("./tools/test-gate.sh task start \"x\" --feature MathQuiz")]
        [TestCase("./tools/test-gate.sh events tail 20")]
        [TestCase("dotnet test LogicTests")]
        public void AllowedCommands(string cmd) => Assert.That(AgentHooks.ForbiddenCommand(cmd), Is.Null);
    }

    public sealed class TestGuardTests
    {
        private const string P = "Assets/_Game/Features/A/Tests/ATests.cs";
        private const string Before = "class T {\n [Test] public void A() { Assert.That(1, Is.EqualTo(1)); Assert.That(2, Is.EqualTo(2)); }\n [Test] public void B() { Assert.That(true); }\n}";

        [Test]
        public void RemovedAssertion_RemovedTest_AddedEscapes_AreFound()
        {
            Assert.That(TestGuard.Check(P, Before, Before.Replace("Assert.That(2, Is.EqualTo(2)); ", "")), Has.Some.Contains("assertions 3 -> 2"));
            Assert.That(TestGuard.Check(P, Before, Before.Replace(" [Test] public void B() { Assert.That(true); }\n", "")), Has.Some.Contains("tests 2 -> 1"));
            Assert.That(TestGuard.Check(P, Before, Before.Replace("[Test] public void B", "[Test, Ignore(\"flaky\")] public void B")), Has.Some.Contains("[Ignore]"));
            Assert.That(TestGuard.Check(P, Before, Before.Replace("{ Assert.That(true); }", "{ Assume.That(false); Assert.That(true); }")), Has.Some.Contains("Assume.That"));
            Assert.That(TestGuard.Check(P, Before, Before.Replace(" [Test] public void B", "#if FALSE\n [Test] public void B").Replace("}\n}", "}\n#endif\n}")), Has.Some.Contains("#if"));
            Assert.That(TestGuard.Check(P, Before, null), Has.Some.Contains("deleted"));
        }

        [Test]
        public void AddingTests_NewFiles_NonTestFiles_AndCommentedAsserts_AreFine()
        {
            Assert.That(TestGuard.Check(P, Before, Before.Replace("}\n}", "}\n [Test] public void C() { Assert.That(3, Is.EqualTo(3)); }\n}")), Is.Empty);
            Assert.That(TestGuard.Check(P, null, "class X {}"), Is.Empty);
            Assert.That(TestGuard.Check("Assets/_Game/Features/A/Logic/X.cs", Before, ""), Is.Empty);
            Assert.That(TestGuard.Check(P, Before + "\n// Assert.That(9);", Before), Is.Empty, "assertion only in a comment");
        }
    }

    public sealed class ReducerTests
    {
        private static long _seq;

        private static AgentEvent E(string type, string source = "hook", string session = "s1", string agent = "main", params string[] kv)
        {
            var e = new AgentEvent { Seq = ++_seq, Ts = "2026-11-18T10:00:" + (_seq % 60).ToString("00") + ".000Z", Type = type, Source = source, Session = session, Agent = agent, Hash = "h" + _seq };
            for (int i = 0; i + 1 < kv.Length; i += 2)
                e.Data[kv[i]] = kv[i + 1];
            return e;
        }

        private static List<AgentEvent> Session()
        {
            _seq = 0;
            return new List<AgentEvent>
            {
                E(EventTypes.SessionStarted),
                E(EventTypes.TaskCreated, "task-cli", kv: new[] { "id", "T1", "status", "active" }),
                E(EventTypes.ToolUsed, kv: new[] { "tool", "Read", "target", "a.cs" }),
                E(EventTypes.ToolUsed, kv: new[] { "tool", "Grep", "target", "Quiz" }),
                E(EventTypes.ToolUsed, kv: new[] { "tool", "Read", "target", "b.cs" }),
                E(EventTypes.FileModified, kv: new[] { "path", "Assets/_Game/Features/A/X.cs", "via", "Edit", "inScope", "true", "after", "aa" }),
                E(EventTypes.PolicyDenied, kv: new[] { "target", "Assets/_Game/Bootstrap/B.cs", "reason", "outside task" }),
                E(EventTypes.AgentSpawned, kv: new[] { "agent", "Explore" }),
                E(EventTypes.ToolUsed, agent: "Explore", kv: new[] { "tool", "Grep", "target", "x" }),
                E(EventTypes.FileModified, "git-observer", kv: new[] { "path", "Assets/_Game/Features/B/Y.cs", "via", "Bash", "inScope", "false", "after", "bb" }),
                E(EventTypes.TestResult, "testgate", "cli", kv: new[] { "suite", "G1 dotnet", "result", "passed" }),
                E(EventTypes.GateFinished, "testgate", "cli", kv: new[] { "result", "passed" }),
                E(EventTypes.UnobservedChange, "git-observer", kv: new[] { "path", "README.md", "after", "cc" }),
                E(EventTypes.Claim, "agent", kv: new[] { "text", "Done, all tests pass." }),
            };
        }

        private static DashboardModel Build(List<AgentEvent> ev) =>
            Reducer.Build(ev, new ChainStatus { Ok = true, Count = ev.Count }, null, "fixed");

        [Test]
        public void SameEvents_SameGraph_TheGraphIsAProjection()
        {
            string a = JsonSerializer.Serialize(Build(Session()));
            string b = JsonSerializer.Serialize(Build(Session()));
            Assert.That(b, Is.EqualTo(a));
        }

        [Test]
        public void TrustLevels_ComeFromTheEventType_NeverFromTheAgent()
        {
            var m = Build(Session());
            Assert.That(m.Steps.Single(s => s.Title.StartsWith("CHẶN")).Trust, Is.EqualTo(Trust.Denied));
            Assert.That(m.Steps.Single(s => s.Title.StartsWith("NGOÀI PHẠM VI")).Trust, Is.EqualTo(Trust.Violation));
            Assert.That(m.Steps.Single(s => s.Kind == "claim").Trust, Is.EqualTo(Trust.Claim));
            Assert.That(m.Files.Single(f => f.Path.EndsWith("A/X.cs")).Trust, Is.EqualTo(Trust.Verified), "changed before a passing gate");
            Assert.That(m.Files.Single(f => f.Path.EndsWith("B/Y.cs")).Trust, Is.EqualTo(Trust.Violation), "a later green gate does not launder a violation");
            Assert.That(m.Files.Single(f => f.Path == "README.md").Trust, Is.EqualTo(Trust.Unobserved));
        }

        [Test]
        public void BrowsingIsCollapsed_SubagentsGetTheirOwnLane()
        {
            var m = Build(Session());
            Assert.That(m.Steps.Count(s => s.Kind == "browse" && s.Lane == "s1/main"), Is.EqualTo(1));
            Assert.That(m.Steps.Single(s => s.Kind == "browse" && s.Lane == "s1/main").Seqs, Has.Count.EqualTo(3));
            var sub = m.Lanes.Single(l => l.Id == "s1/Explore");
            Assert.That(sub.Kind, Is.EqualTo("subagent"));
            Assert.That(sub.Parent, Is.EqualTo("s1/main"));
            Assert.That(m.Lanes.Single(l => l.Id == "cli/main").Kind, Is.EqualTo("gate"));
        }

        [Test]
        public void AClaim_IsJudgedByEvidence()
        {
            var m = Build(Session());
            var claim = m.Claims.Single();
            Assert.That(claim.Text, Is.EqualTo("Done, all tests pass."));
            Assert.That(claim.Verdict, Is.EqualTo("mâu thuẫn với bằng chứng"), "a green gate, but the session also broke scope");
            Assert.That(claim.Evidence, Has.Some.Contains("vi phạm"));

            _seq = 0;
            var blocked = new List<AgentEvent>
            {
                E(EventTypes.GateFinished, "testgate", kv: new[] { "result", "passed" }),
                E(EventTypes.StopBlocked, "testgate", kv: new[] { "reason", "TESTS assertions 24 -> 23" }),
                E(EventTypes.Claim, "agent", kv: new[] { "text", "All green." }),
            };
            var bc = Build(blocked).Claims.Single();
            Assert.That(bc.Verdict, Is.EqualTo("mâu thuẫn với bằng chứng"), "a blocked Stop contradicts 'all green'");
            Assert.That(bc.Evidence, Has.Some.Contains("assertions 24 -> 23"));

            _seq = 0;
            var noGate = new List<AgentEvent> { E(EventTypes.Claim, "agent", kv: new[] { "text", "Fixed!" }) };
            Assert.That(Build(noGate).Claims.Single().Verdict, Is.EqualTo("không có bằng chứng"));
        }
    }

    /// <summary>End to end on a throw-away git repo: the hooks as Claude Code would call them.</summary>
    public sealed class HookFlowTests
    {
        private string _root;

        private void Git(string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private static HookInput H(string tool, string key = null, string value = null) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sess1",
                ["tool_name"] = tool,
                ["tool_use_id"] = "tu1",
                ["tool_input"] = key == null ? new Dictionary<string, string>() : new Dictionary<string, string> { [key] = value }
            }));

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "hookflow-" + Guid.NewGuid().ToString("N"));
            Write("Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Write("Assets/_Game/Features/B/Logic/Y.cs", "class Y {}\n");
            Write("Assets/_Game/Features/A/Tests/ATests.cs", "class T { [Test] public void A() { Assert.That(1, Is.EqualTo(1)); } }\n");
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            Recorder.Session = "sess1";
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        private static List<string> NoRules(IEnumerable<string> files) => new List<string>();

        private List<AgentEvent> Events() => EventLog.For(_root).ReadAll();

        [Test]
        public void WithoutATask_EditsAreBlockedBeforeTheyHappen()
        {
            int code = AgentHooks.Pre(_root, H("Edit", "file_path", Path.Combine(_root, "Assets/_Game/Features/A/Logic/X.cs")));
            Assert.That(code, Is.EqualTo(2));
            Assert.That(Events().Single().Type, Is.EqualTo(EventTypes.PolicyDenied));
        }

        [Test]
        public void ScopeGuard_EditInScopePasses_OutOfScopeIsBlocked()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "fix A", new[] { "A" }, new string[0], false, "now"));
            Assert.That(AgentHooks.Pre(_root, H("Edit", "file_path", "Assets/_Game/Features/A/Logic/X.cs")), Is.EqualTo(0));
            Assert.That(AgentHooks.Pre(_root, H("Write", "file_path", "Assets/_Game/Features/B/Logic/Y.cs")), Is.EqualTo(2));

            Write("Assets/_Game/Features/A/Logic/X.cs", "class X { int a; }\n");
            Assert.That(AgentHooks.Post(_root, H("Edit", "file_path", "Assets/_Game/Features/A/Logic/X.cs"), NoRules), Is.EqualTo(0));
            var mod = Events().Single(e => e.Type == EventTypes.FileModified);
            Assert.That(mod.Get("inScope"), Is.EqualTo("true"));
            Assert.That(mod.Get("after"), Is.EqualTo(WorkSnapshot.HashFile(Path.Combine(_root, "Assets/_Game/Features/A/Logic/X.cs"))));
        }

        [Test]
        public void AtStop_AnObservedOutOfScopeChange_BlocksFinishing()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "fix A", new[] { "A" }, new string[0], false, "now"));
            var bash = H("Bash", "command", "echo > Y.cs");
            AgentHooks.Pre(_root, bash);
            Write("Assets/_Game/Features/B/Logic/Y.cs", "class Sneaky {}\n");
            AgentHooks.Post(_root, bash, NoRules);
            Assert.That(AgentHooks.StopChecks(_root, H(null), Original), Has.Some.StartsWith("SCOPE Assets/_Game/Features/B/Logic/Y.cs"));
        }

        [Test]
        public void ABashEdit_IsObservedByTheGitSnapshot_AndOutOfScopeIsAViolation()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "fix A", new[] { "A" }, new string[0], false, "now"));
            var bash = H("Bash", "command", "sed -i 's/Y/Z/' Assets/_Game/Features/B/Logic/Y.cs");
            Assert.That(AgentHooks.Pre(_root, bash), Is.EqualTo(0));
            Write("Assets/_Game/Features/B/Logic/Y.cs", "class Z {}\n"); // what sed did
            int code = AgentHooks.Post(_root, bash, NoRules);

            Assert.That(code, Is.EqualTo(2), "fed back to the agent");
            var ev = Events();
            var mod = ev.Single(e => e.Type == EventTypes.FileModified);
            Assert.That(mod.Source, Is.EqualTo("git-observer"));
            Assert.That(mod.Get("via"), Is.EqualTo("Bash"));
            Assert.That(mod.Get("inScope"), Is.EqualTo("false"));
            Assert.That(ev.Any(e => e.Type == EventTypes.PolicyViolation), Is.True);
            Assert.That(EventLog.For(_root).Verify().Ok, Is.True);
        }

        [Test]
        public void AtStop_UnobservedChanges_AreRecorded_WeakenedTestsAreFoundWhoeverChangedThem()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "fix A", new[] { "A" }, new string[0], false, "now"));
            Write("Assets/_Game/Features/B/Logic/Y.cs", "class Sneaky {}\n");                                  // no tool call saw this
            Write("Assets/_Game/Features/A/Tests/ATests.cs", "class T { [Test, Ignore(\"later\")] public void A() { } }\n"); // fake fix

            var findings = AgentHooks.StopChecks(_root, H(null), Original);

            Assert.That(findings.Any(f => f.StartsWith("SCOPE")), Is.False, "nobody SAW the agent change B: it is UNOBSERVED, not a scope breach");
            Assert.That(findings, Has.Some.Contains("assertions 1 -> 0"));
            Assert.That(findings, Has.Some.Contains("[Ignore]"));
            var ev = Events();
            Assert.That(ev.Count(e => e.Type == EventTypes.UnobservedChange), Is.EqualTo(2));
        }

        // ------------------------------------------------------------------ MCP (Unity, Blender): observed like Bash, never "the human's"

        private const string Prefab = "Assets/_Game/Features/A/Assets/Private/Boss.prefab";
        private const string OtherPrefab = "Assets/_Game/Features/B/Assets/Private/Boss.prefab";

        [Test]
        public void McpWrite_NeedsATask_AndATargetInScope()
        {
            Assert.That(AgentHooks.Pre(_root, H("mcp__unity__manage_prefabs", "path", Prefab)), Is.EqualTo(2), "no task");
            TaskStore.Save(_root, ScopePolicy.Start("T1", "boss", new[] { "A" }, new string[0], false, "now"));
            Assert.That(AgentHooks.Pre(_root, H("mcp__unity__manage_prefabs", "path", Prefab)), Is.EqualTo(0));
            Assert.That(AgentHooks.Pre(_root, H("mcp__unity__manage_prefabs", "prefab_path", OtherPrefab)), Is.EqualTo(2), "named target outside the task");
            Assert.That(AgentHooks.Pre(_root, H("mcp__unity__get_hierarchy")), Is.EqualTo(0), "read tools need nothing");
        }

        [Test]
        public void WhatAnMcpToolChanges_IsTheAgents_AndOutOfScopeSideEffectsBlockStop()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "boss", new[] { "A" }, new string[0], false, "now"));
            var mcp = H("mcp__unity__manage_prefabs", "path", Prefab);
            Assert.That(AgentHooks.Pre(_root, mcp), Is.EqualTo(0));
            Write(Prefab, "--- !u!1 &1\nGameObject:\n  m_Name: Boss\n");
            Write(OtherPrefab, "--- !u!1 &1\nGameObject:\n  m_Name: Sneaky\n");   // a side effect the tool input never named
            Assert.That(AgentHooks.Post(_root, mcp, NoRules), Is.EqualTo(2), "fed back at once");

            var mods = Events().Where(e => e.Type == EventTypes.FileModified).ToList();
            Assert.That(mods.Select(e => e.Get("path")), Is.EquivalentTo(new[] { Prefab, OtherPrefab }));
            Assert.That(mods.All(e => e.Source == "git-observer" && e.Get("via") == "mcp__unity__manage_prefabs"), Is.True);
            Assert.That(Attribution.AuthorOf(OtherPrefab, Attribution.Authors(Events())), Is.EqualTo("agent"));
            Assert.That(AgentHooks.StopChecks(_root, H(null), Original), Has.Some.StartsWith("SCOPE " + OtherPrefab));
        }

        [Test]
        public void CodeRunningTools_AreBlocked_UnlessTheOwnerAllowsThem()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "boss", new[] { "A" }, new string[0], false, "now"));
            var exec = H("mcp__blender__execute_blender_code", "code", "import bpy");
            Assert.That(AgentHooks.Pre(_root, exec), Is.EqualTo(2));
            Write(McpPolicy.File_, "exec mcp__blender__execute_blender_code\n");
            Assert.That(AgentHooks.Pre(_root, exec), Is.EqualTo(0), "allowed by the owner: still observed like a write");
            Write(McpPolicy.File_, "exec mcp__blender__*\ndeny mcp__blender__execute_blender_code\n");
            Assert.That(AgentHooks.Pre(_root, exec), Is.EqualTo(2), "last matching line wins");
        }

        [Test]
        public void UnityWritingThePrefabLater_IsStillJudgedAsTheAgents()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "boss", new[] { "A" }, new string[0], false, "now"));
            var mcp = H("mcp__unity__manage_gameobject", "name", "Boss");
            AgentHooks.Pre(_root, mcp);
            AgentHooks.Post(_root, mcp, NoRules);                       // the editor kept it in memory: nothing on disk yet
            Write(OtherPrefab, "--- !u!1 &1\nGameObject:\n  m_Name: Boss\n");  // ...and saved it later
            Write("Assets/_Game/Features/B/Logic/Y.cs", "class ByAHuman {}\n");      // code in the IDE: still the human's

            var findings = AgentHooks.StopChecks(_root, H(null), Original);
            Assert.That(findings, Has.Some.StartsWith("SCOPE " + OtherPrefab));
            Assert.That(findings.Any(f => f.Contains("Y.cs")), Is.False);
            Assert.That(Events().Single(e => e.Type == EventTypes.UnobservedChange && e.Get("path") == OtherPrefab).Get("likely"), Is.EqualTo("agent"));
        }

        private string Original(string path)
        {
            var p = Process.Start(new ProcessStartInfo("git", "show HEAD:" + path) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? o : null;
        }
    }
}

namespace LogicTests.AgentControl
{
    public sealed class RestoreTests
    {
        [Test]
        public void UndoingAnOutOfScopeChange_IsNotAViolation()
        {
            string root = Path.Combine(Path.GetTempPath(), "restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets/_Game/Features/B"));
            File.WriteAllText(Path.Combine(root, "Assets/_Game/Features/B/Y.cs"), "class Y {}\n");
            void Git(string a)
            {
                var p = Process.Start(new ProcessStartInfo("git", a) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            }
            Git("init -q"); Git("add -A"); Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = root;
            try
            {
                TaskStore.Save(root, ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now"));
                var bash = HookInput.Parse("{\"session_id\":\"s\",\"tool_name\":\"Bash\",\"tool_use_id\":\"u\",\"tool_input\":{\"command\":\"git checkout -- Y.cs\"}}");
                File.WriteAllText(Path.Combine(root, "Assets/_Game/Features/B/Y.cs"), "class Sneaky {}\n");
                AgentHooks.Pre(root, bash);
                Git("checkout -- Assets/_Game/Features/B/Y.cs");
                Assert.That(AgentHooks.Post(root, bash, _ => new List<string>()), Is.EqualTo(0));
                var mod = EventLog.For(root).ReadAll().Single(e => e.Type == EventTypes.FileModified);
                Assert.That(mod.Get("inScope"), Is.EqualTo("true"));
                Assert.That(mod.Get("scope"), Does.Contain("restored"));
            }
            finally
            {
                Recorder.Root = null;
                DeleteTree(root); // git marks its objects read-only: a plain Directory.Delete fails on Windows
            }
        }

        private static void DeleteTree(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }
}
