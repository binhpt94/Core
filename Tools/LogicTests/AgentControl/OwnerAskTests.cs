using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// Owner commands in an agent's shell: only the plain form, only after the owner's yes in Claude Code (ask), one yes = one run of
    /// exactly the args shown. Anything the hook can't read as one plain command stays blocked.
    /// </summary>
    public sealed class OwnerAskTests
    {
        private const string Plan = "# Plan: T\n\n## F-A  Feature A  [P1] [modules: A]\n- R-A-ONE  Một  [manual]\n- R-A-TWO  Hai\n";
        private const string Approve = "./Tools/test-gate.sh task approve";
        private string _root;
        private string _shell, _grant;
        private Func<DateTime> _clock;

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

        private static HookInput Bash(string cmd, string id) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sess1", ["tool_name"] = "Bash", ["tool_use_id"] = id,
                ["tool_input"] = new Dictionary<string, object> { ["command"] = cmd, ["description"] = "d", ["timeout"] = 60000 }
            }));

        /// <summary>Runs PreToolUse and returns (exit code, stdout).</summary>
        private (int code, string stdout) Pre(string cmd, string id = "tu1")
        {
            var old = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { return (AgentHooks.Pre(_root, Bash(cmd, id)), sw.ToString()); }
            finally { Console.SetOut(old); }
        }

        private static JsonElement Specific(string stdout) => JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");

        private static string Nonce(string stdout)
        {
            string cmd = OwnerAsk.RunLine(Specific(stdout).GetProperty("updatedInput").GetProperty("command").GetString());
            Assert.That(cmd, Does.StartWith(OwnerAsk.GrantVariable + "="));
            return cmd.Substring(OwnerAsk.GrantVariable.Length + 1).Split(' ')[0];
        }

        private static List<string> NoRules(IEnumerable<string> files) => new List<string>();

        private AgentTask Pending()
        {
            var t = ScopePolicy.Start("T1", "x", new[] { "A", "B" }, new string[0], false, "now");
            TaskStore.Save(_root, t);
            return t;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ownerask-" + Guid.NewGuid().ToString("N"));
            Write("UnityProject/Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Write(PlanParser.RelativePath, Plan);
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            Recorder.Session = "sess1";
            _shell = Environment.GetEnvironmentVariable("GAME_AGENT_SHELL");
            _grant = Environment.GetEnvironmentVariable(OwnerAsk.GrantVariable);
            Environment.SetEnvironmentVariable("GAME_AGENT_SHELL", "test");   // never a human terminal here, also in CI
            _clock = OwnerAsk.Clock;
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("GAME_AGENT_SHELL", _shell);
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, _grant);
            OwnerAsk.Clock = _clock;
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        [Test]
        public void ThePlainForm_AsksTheOwner_WithWhatTheGateComputed_AndKeepsTheRestOfTheInput()
        {
            Pending();
            var (code, stdout) = Pre(Approve);
            Assert.That(code, Is.EqualTo(0), "ask = exit 0 + JSON on stdout");
            var s = Specific(stdout);
            Assert.That(s.GetProperty("permissionDecision").GetString(), Is.EqualTo("ask"));
            Assert.That(s.GetProperty("permissionDecisionReason").GetString(), Does.Contain("\"x\"").And.Contain("Features/B"));
            var input = s.GetProperty("updatedInput");
            string command = input.GetProperty("command").GetString();
            Assert.That(OwnerAsk.RunLine(command), Does.StartWith(OwnerAsk.GrantVariable + "=").And.EndWith(" " + Approve));
            var above = command.Split('\n').Reverse().Skip(1).ToList();
            Assert.That(above, Is.Not.Empty.And.All.StartWith("#"), "the explanation is comment lines only: nothing in it runs");
            Assert.That(command, Does.Contain("# • Bấm Yes:"), "one section per line in the monospace box");
            Assert.That(input.GetProperty("description").GetString(), Does.StartWith("❓").And.Not.Contain("\n"),
                "the description is the gate's question, not the agent's words");
            Assert.That(input.GetProperty("timeout").GetInt32(), Is.EqualTo(60000));
            Assert.That(EventLog.For(_root).ReadAll().Any(e => e.Type == OwnerAsk.AskedEvent && e.Get("key") == "task\u001fapprove"), Is.True);
        }

        [TestCase(Approve + "; echo done")]
        [TestCase(Approve + " && git status")]
        [TestCase(Approve + " | cat")]
        [TestCase("bash -c \"" + Approve + "\"")]
        [TestCase("echo $(" + Approve + ")")]
        [TestCase(Approve + " > out.txt")]
        [TestCase("dotnet Tools/TestGate/bin/Release/net8.0/TestGate.dll task approve")]
        [TestCase("GAME_OWNER_GRANT=abc " + Approve)]
        [TestCase("export LAYOUT_WRITE=1")]
        [TestCase("GOLDEN_WRITE=1 dotnet test Tools/LogicTests && git add -A")]
        public void AnythingButThePlainForm_StaysBlocked(string cmd)
        {
            Pending();
            Assert.That(Pre(cmd).code, Is.EqualTo(2));
            Assert.That(EventLog.For(_root).ReadAll().Any(e => e.Type == OwnerAsk.AskedEvent), Is.False, "never asked");
        }

        [Test]
        public void NothingValidToDecide_IsBlocked_WithoutAskingTheOwner()
        {
            Assert.That(OwnerAsk.Decide(_root, Approve).Kind, Is.EqualTo(OwnerAskDecision.Deny), "no pending task");
            Assert.That(OwnerAsk.Decide(_root, "./Tools/test-gate.sh roadmap accept R-NOPE").Kind, Is.EqualTo(OwnerAskDecision.Deny));
            Assert.That(OwnerAsk.Decide(_root, "./Tools/test-gate.sh tests unseal R-A-ONE").Kind, Is.EqualTo(OwnerAskDecision.Deny), "why is required");
            var loop = OwnerAsk.Decide(_root, "./Tools/test-gate.sh cr decide");
            Assert.That(loop.Kind, Is.EqualTo(OwnerAskDecision.Deny));
            Assert.That(loop.Reason, Does.Contain("cr decide CR-n approve|reject"));
            Assert.That(OwnerAsk.Decide(_root, "./Tools/test-gate.sh cr decide CR-9 approve \"ok\"").Kind, Is.EqualTo(OwnerAskDecision.Deny), "unknown CR");
        }

        [Test]
        public void MentioningAnOwnerCommand_IsNotAnOwnerCommand()
        {
            Assert.That(OwnerAsk.Decide(_root, "git commit -m \"task approve via ask\""), Is.Null);
            Assert.That(OwnerAsk.Decide(_root, "grep -rn \"roadmap accept\" Docs"), Is.Null);
            Assert.That(OwnerAsk.Decide(_root, "grep -n 'GOLDEN_WRITE=1' Docs/ARCHITECTURE.md"), Is.Null, "a quoted mention is not an assignment");
            Assert.That(OwnerAsk.Decide(_root, "grep -n \"GAME_OWNER_GRANT=\" Tools"), Is.Null);
        }

        [Test]
        public void TheDialog_SaysInPlainWordsWhatYesAndNoDo()
        {
            var d = OwnerAsk.Decide(_root, "./Tools/test-gate.sh roadmap accept R-A-ONE");
            Assert.That(d.Reason, Does.Contain("\"Một\"").And.Contain("Feature A"), "the requirement in words, the feature by its title");
            Assert.That(d.Reason, Does.Contain("Bấm Yes:").And.Contain("Bấm No:").And.Contain("Nên kiểm tra trước:").And.Contain("Hoàn tác:"));
            Assert.That(d.Reason, Does.Contain(OwnerAskText.GrantNote), "explains the GAME_OWNER_GRANT prefix the owner sees in the command");
            Pending();
            var t = OwnerAsk.Decide(_root, Approve);
            Assert.That(t.Reason, Does.Contain("\"x\"").And.Contain("Bấm Yes:"), "the task's title, not only its id");
            var golden = OwnerAsk.Decide(_root, "GOLDEN_WRITE=1 dotnet test Tools/LogicTests");
            Assert.That(golden.Reason, Does.Contain("Chưa có file code luật chơi"), "no gameplay change: says so instead of claiming the replay drifted");
            Assert.That(golden.Reason, Does.Not.Contain("GAME_OWNER_GRANT"), "a baseline rewrite has no grant prefix");
        }

        [Test]
        public void TheOwnersYes_RunsTheCommandOnce_AsTheOwner()
        {
            Pending();
            var (_, stdout) = Pre(Approve);
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, Nonce(stdout));
            Assert.That(AgentCli.Task(_root, new[] { "task", "approve" }), Is.EqualTo(0));
            Assert.That(TaskStore.Load(_root).IsActive, Is.True);
            var approved = EventLog.For(_root).ReadAll().Single(e => e.Type == EventTypes.TaskApproved);
            Assert.That(approved.Source, Is.EqualTo(OwnerAsk.Source));
            Assert.That(approved.Get("toolUseId"), Is.EqualTo("tu1"));
            Assert.That(OwnerAsk.TryConsume(_root, new[] { "task", "approve" }, Nonce(stdout), out var why), Is.Null);
            Assert.That(why, Does.Contain("already used"));
        }

        [Test]
        public void WhatAnApprovedOwnerCommandWrites_IsInScope_EvenInAFeatureTask()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T2", "x", new[] { "A" }, new string[0], false, "now"));
            var (_, stdout) = Pre("./Tools/test-gate.sh roadmap accept R-A-ONE", "tu1");
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, Nonce(stdout));
            Assert.That(TestGate.Planning.RoadmapCli.Run(_root, new[] { "roadmap", "accept", "R-A-ONE" }), Is.EqualTo(0));
            Assert.That(AgentHooks.Post(_root, Bash("./Tools/test-gate.sh roadmap accept R-A-ONE", "tu1"), NoRules), Is.EqualTo(0), "no scope finding");
            string state = WorkSnapshot.HashFile(Path.Combine(_root, PlanParser.AcceptedPath));
            Assert.That(OwnerAsk.IsApprovedBaseline(_root, PlanParser.AcceptedPath, state), Is.True);

            // The same file written by an unasked command is still the owner's, not the agent's.
            Pre("echo x", "tu2");
            File.AppendAllText(Path.Combine(_root, PlanParser.AcceptedPath), "R-A-TWO forged\n");
            Assert.That(AgentHooks.Post(_root, Bash("echo x", "tu2"), NoRules), Is.EqualTo(2));
        }

        [Test]
        public void AnAgentShell_WithoutAGrant_IsRefused()
        {
            Pending();
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, null);
            Assert.That(AgentCli.Task(_root, new[] { "task", "approve" }), Is.EqualTo(3));
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, "forged");
            Assert.That(AgentCli.Task(_root, new[] { "task", "approve" }), Is.EqualTo(3));
            Assert.That(TaskStore.Load(_root).IsActive, Is.False);
        }

        [Test]
        public void AGrant_IsForTheExactArgsShown()
        {
            var (code, stdout) = Pre("./Tools/test-gate.sh roadmap accept R-A-ONE");
            Assert.That(code, Is.EqualTo(0));
            Assert.That(Specific(stdout).GetProperty("permissionDecisionReason").GetString(), Does.Contain("R-A-ONE").And.Contain("Một"));
            Assert.That(OwnerAsk.TryConsume(_root, new[] { "roadmap", "accept", "R-A-TWO" }, Nonce(stdout), out var why), Is.Null);
            Assert.That(why, Does.Contain("other arguments"));
            Assert.That(OwnerAsk.TryConsume(_root, new[] { "roadmap", "accept", "R-A-ONE" }, Nonce(stdout), out _), Is.Not.Null);
        }

        [Test]
        public void AGrant_Expires()
        {
            var (_, stdout) = Pre("./Tools/test-gate.sh roadmap accept R-A-ONE");
            OwnerAsk.Clock = () => DateTime.UtcNow + OwnerAsk.Ttl + TimeSpan.FromMinutes(1);
            Assert.That(OwnerAsk.TryConsume(_root, new[] { "roadmap", "accept", "R-A-ONE" }, Nonce(stdout), out var why), Is.Null);
            Assert.That(why, Does.Contain("expired"));
        }

        [Test]
        public void AGrantTheOwnerSaidNoTo_IsRevokedBeforeTheNextToolCall()
        {
            var (_, stdout) = Pre("./Tools/test-gate.sh roadmap accept R-A-ONE", "tu1");
            Assert.That(Pre("git status", "tu2").code, Is.EqualTo(0));
            Assert.That(OwnerAsk.TryConsume(_root, new[] { "roadmap", "accept", "R-A-ONE" }, Nonce(stdout), out var why), Is.Null);
            Assert.That(why, Does.Contain("revoked"));
        }

        [Test]
        public void ABaselineRewrite_IsAsked_AndOnlyWhatItWroteCountsAsApproved()
        {
            const string replay = "UnityProject/Assets/_Game/Features/A/Tests/Replays/one.replay.txt";
            var (code, stdout) = Pre("GOLDEN_WRITE=1 dotnet test Tools/LogicTests", "tu1");
            Assert.That(code, Is.EqualTo(0));
            var s = Specific(stdout);
            Assert.That(s.GetProperty("permissionDecision").GetString(), Is.EqualTo("ask"));
            var input = s.GetProperty("updatedInput");
            Assert.That(OwnerAsk.RunLine(input.GetProperty("command").GetString()), Is.EqualTo("GOLDEN_WRITE=1 dotnet test Tools/LogicTests"), "the tests don't check the shell: no grant prefix");
            Assert.That(input.GetProperty("command").GetString(), Does.Contain("# ❓ Ghi lại các bản chơi mẫu"));
            Write(replay, "tick 1\n");
            AgentHooks.Post(_root, Bash("GOLDEN_WRITE=1 dotnet test Tools/LogicTests", "tu1"), NoRules);
            Assert.That(OwnerAsk.IsApprovedBaseline(_root, replay, WorkSnapshot.HashFile(Path.Combine(_root, replay))), Is.True);

            // The same file rewritten by a command nobody was asked about: not approved.
            Pre("echo x", "tu2");
            Write(replay, "tick 2\n");
            AgentHooks.Post(_root, Bash("echo x", "tu2"), NoRules);
            Assert.That(OwnerAsk.IsApprovedBaseline(_root, replay, WorkSnapshot.HashFile(Path.Combine(_root, replay))), Is.False);
        }

        [Test]
        public void ALayoutRewrite_ShowsWhichViews()
        {
            var d = OwnerAsk.Decide(_root, "LAYOUT_WRITE=1 ./Tools/run-unity-tests.sh EditMode \"View:MathQuiz.QuizView\"");
            Assert.That(d.Kind, Is.EqualTo(OwnerAskDecision.Ask));
            Assert.That(d.Baseline, Is.True);
            Assert.That(d.Reason, Does.Contain("Màn hình: MathQuiz.QuizView"));
        }

        [Test]
        public void TheCommentedCommand_CannotSmuggleARunningLine()
        {
            string c = OwnerAsk.Commented("a\nb\r\nrm -rf x \\\nc", "echo ok");
            var lines = c.Split('\n');
            Assert.That(lines.Take(lines.Length - 1), Is.All.StartWith("# "));
            Assert.That(c, Does.Not.Contain("\\"), "no backslash: no line continuation out of a comment");
            Assert.That(OwnerAsk.RunLine(c), Is.EqualTo("echo ok"));
        }

        [Test]
        public void Tokenize_TakesLiteralWordsOnly()
        {
            Assert.That(OwnerAsk.Tokenize("a 'b c' \"d e\" f=g"), Is.EqualTo(new[] { "a", "b c", "d e", "f=g" }));
            Assert.That(OwnerAsk.Tokenize("a \"$HOME\""), Is.Null);
            Assert.That(OwnerAsk.Tokenize("a 'unterminated"), Is.Null);
            Assert.That(OwnerAsk.Tokenize("a\nb"), Is.Null);
            Assert.That(OwnerAsk.Tokenize("a *"), Is.Null);
        }
    }
}
