using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// Owner files that used to be edited by hand (PublicApi.txt baselines, Tools/ArchitecturePolicy.txt, Tools/mcp-policy.txt)
    /// change through one owner command each: the owner sees exactly what will be written (yes/no), the grant is bound to that
    /// content, and an invalid request is refused before anyone is asked.
    /// </summary>
    public sealed class OwnerPolicyTests
    {
        private const string Core = "UnityProject/Assets/_Game/Core/Runtime/PublicApi.txt";
        private const string Received = "Dashboard/Data/api/Game.Core.received.txt";
        private const string OldApi = "sealed class Game.Core.A\n  System.Int32 X { get; }\n";
        private const string NewApi = "sealed class Game.Core.A\n  System.Int32 X { get; }\n  System.Int32 Y { get; }\n";
        private string _root, _shell, _grant;
        private Func<string, string> _regenerate;

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

        private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel));

        private static HookInput Bash(string cmd, string id) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sess1", ["tool_name"] = "Bash", ["tool_use_id"] = id,
                ["tool_input"] = new Dictionary<string, object> { ["command"] = cmd, ["description"] = "d" }
            }));

        private (int code, string stdout) Pre(string cmd, string id = "tu1")
        {
            var old = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { return (AgentHooks.Pre(_root, Bash(cmd, id)), sw.ToString()); }
            finally { Console.SetOut(old); }
        }

        private static JsonElement Specific(string stdout) => JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");

        /// <summary>Asks (the hook must return "ask"), then puts the grant in the environment as Claude Code's command would.</summary>
        private string AskAndGrant(string cmd, string id = "tu1")
        {
            var (code, stdout) = Pre(cmd, id);
            Assert.That(code, Is.EqualTo(0));
            var s = Specific(stdout);
            Assert.That(s.GetProperty("permissionDecision").GetString(), Is.EqualTo("ask"), stdout);
            string run = OwnerAsk.RunLine(s.GetProperty("updatedInput").GetProperty("command").GetString());
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, run.Substring(OwnerAsk.GrantVariable.Length + 1).Split(' ')[0]);
            return s.GetProperty("permissionDecisionReason").GetString();
        }

        private string DeniedReason(string cmd)
        {
            var d = OwnerAsk.Decide(_root, cmd);
            Assert.That(d, Is.Not.Null, cmd);
            Assert.That(d.Kind, Is.EqualTo(OwnerAskDecision.Deny), cmd + " → " + d.Reason);
            return d.Reason;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ownerpolicy-" + Guid.NewGuid().ToString("N"));
            Write(Core, OldApi);
            Write("Tools/ArchitecturePolicy.txt", "# policy\nvendor id=unitask tier=foundation assemblies=UniTask package=com.cysharp.unitask version=2.5.10\n");
            Write("Tools/mcp-policy.txt", "# mcp\ndeny  mcp__blender__execute_blender_code\n");
            Write("UnityProject/Packages/manifest.json", "{ \"dependencies\": { \"com.cysharp.unitask\": \"2.5.10\", \"com.demigiant.dotween\": \"1.2.765\" } }\n");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Game.A.Unity.asmdef", "{ \"name\": \"Game.A.Unity\", \"references\": [\"DOTween\"] }\n");
            Write("UnityProject/Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            Recorder.Session = "sess1";
            _shell = Environment.GetEnvironmentVariable("GAME_AGENT_SHELL");
            _grant = Environment.GetEnvironmentVariable(OwnerAsk.GrantVariable);
            Environment.SetEnvironmentVariable("GAME_AGENT_SHELL", "test");
            _regenerate = ApiBaseline.Regenerate;
            ApiBaseline.Regenerate = _ => null; // the received file the test wrote stands for the compiled API
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("GAME_AGENT_SHELL", _shell);
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, _grant);
            ApiBaseline.Regenerate = _regenerate;
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        // ------------------------------------------------------------------ api accept

        [Test]
        public void ApiAccept_ShowsTheNewLines_AndYesWritesExactlyThem_InScopeForAnyTask()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now"));
            Write(Received, NewApi);
            string reason = AskAndGrant("./Tools/test-gate.sh api accept Game.Core");
            Assert.That(reason, Does.Contain("+ System.Int32 Y { get; }").And.Contain("Chỉ THÊM"));

            Assert.That(ApiBaseline.Cli(_root, new[] { "api", "accept", "Game.Core" }), Is.EqualTo(0));
            Assert.That(Read(Core), Is.EqualTo(NewApi));
            Assert.That(EventLog.For(_root).ReadAll().Any(e => e.Type == ApiBaseline.AcceptedEvent && e.Source == OwnerAsk.Source), Is.True);
            Assert.That(AgentHooks.Post(_root, Bash("./Tools/test-gate.sh api accept Game.Core", "tu1"), _ => new List<string>()), Is.EqualTo(0),
                "the baseline the owner approved is not a scope breach of a feature task");
            Assert.That(OwnerAsk.IsApprovedBaseline(_root, Core, WorkSnapshot.HashFile(Path.Combine(_root, Core))), Is.True);
        }

        [Test]
        public void ApiAccept_RemovedLines_AreShownAsBreaking()
        {
            Write(Received, "sealed class Game.Core.A\n");
            string reason = OwnerAsk.Decide(_root, "./Tools/test-gate.sh api accept Game.Core").Reason;
            Assert.That(reason, Does.Contain("CÓ PHÁ VỠ").And.Contain("- System.Int32 X { get; }"));
        }

        [Test]
        public void ApiAccept_WhenTheApiChangedAfterTheOwnerWasAsked_WritesNothing()
        {
            Write(Received, NewApi);
            AskAndGrant("./Tools/test-gate.sh api accept Game.Core");
            ApiBaseline.Regenerate = root => { File.WriteAllText(Path.Combine(root, Received), NewApi + "  System.Int32 Z { get; }\n"); return null; };
            Assert.That(ApiBaseline.Cli(_root, new[] { "api", "accept", "Game.Core" }), Is.EqualTo(1));
            Assert.That(Read(Core), Is.EqualTo(OldApi));
        }

        [Test]
        public void ApiAccept_NothingToAccept_UnknownGroup_OrChained_IsRefusedWithoutAsking()
        {
            Assert.That(DeniedReason("./Tools/test-gate.sh api accept Game.Core"), Does.Contain("no new API recorded"));
            Write(Received, OldApi);
            Assert.That(DeniedReason("./Tools/test-gate.sh api accept Game.Core"), Does.Contain("already matches"));
            Assert.That(DeniedReason("./Tools/test-gate.sh api accept Game.Nope.Contracts"), Does.Contain("unknown API group"));
            Write(Received, NewApi);
            Assert.That(DeniedReason("./Tools/test-gate.sh api accept Game.Core && git add -A"), Does.Contain("alone"));
        }

        [Test]
        public void ApiAccept_WithoutAGrant_IsRefused()
        {
            Write(Received, NewApi);
            Environment.SetEnvironmentVariable(OwnerAsk.GrantVariable, null);
            Assert.That(ApiBaseline.Cli(_root, new[] { "api", "accept", "Game.Core" }), Is.EqualTo(3));
            Assert.That(Read(Core), Is.EqualTo(OldApi));
        }

        // ------------------------------------------------------------------ policy vendor

        [Test]
        public void VendorAdd_ValidatedLine_YesAppendsExactlyIt()
        {
            const string cmd = "./Tools/test-gate.sh policy vendor add id=dotween tier=presentation assemblies=DOTween package=com.demigiant.dotween version=1.2.765";
            string reason = AskAndGrant(cmd);
            Assert.That(reason, Does.Contain("Game.A.Unity").And.Contain("lớp Unity"), "who references it today, what the tier means");
            Assert.That(PolicyCli.Cli(_root, cmd.Split(' ').Skip(1).ToArray()), Is.EqualTo(0));
            Assert.That(Read("Tools/ArchitecturePolicy.txt"), Does.EndWith(
                "\nvendor id=dotween tier=presentation assemblies=DOTween package=com.demigiant.dotween version=1.2.765\n"));
            Assert.That(AgentHooks.Post(_root, Bash(cmd, "tu1"), _ => new List<string>()), Is.EqualTo(0));
        }

        [TestCase("id=dotween tier=presentation assemblies=DOTween package=com.demigiant.dotween version=9.9", "manifest pins")]
        [TestCase("id=dotween tier=presentation assemblies=DOTween package=com.nope version=1", "not in")]
        [TestCase("id=unitask tier=foundation assemblies=UniTask2 package=com.demigiant.dotween version=1.2.765", "already registered")]
        [TestCase("id=x tier=sdk assemblies=X package=com.demigiant.dotween version=1.2.765", "adapter")]
        [TestCase("id=x tier=foundation assemblies=X package=com.demigiant.dotween version=1.2.765 sha256=abc", "computed by the gate")]
        public void VendorAdd_InvalidLine_IsRefusedWithoutAsking(string kv, string why)
        {
            Assert.That(DeniedReason("./Tools/test-gate.sh policy vendor add " + kv), Does.Contain(why));
        }

        [Test]
        public void VendorUpdate_RepinsToTheManifest()
        {
            Write("UnityProject/Packages/manifest.json", "{ \"dependencies\": { \"com.cysharp.unitask\": \"2.5.11\" } }\n");
            const string cmd = "./Tools/test-gate.sh policy vendor update unitask";
            Assert.That(AskAndGrant(cmd), Does.Contain("version=2.5.11"));
            Assert.That(PolicyCli.Cli(_root, new[] { "policy", "vendor", "update", "unitask" }), Is.EqualTo(0));
            Assert.That(Read("Tools/ArchitecturePolicy.txt"), Does.Contain("version=2.5.11").And.Not.Contain("version=2.5.10"));
        }

        // ------------------------------------------------------------------ mcp allow

        [Test]
        public void McpAllow_Exec_WarnsAndAppendsOneLine()
        {
            const string cmd = "./Tools/test-gate.sh mcp allow exec mcp__unity__execute_code";
            Assert.That(AskAndGrant(cmd), Does.Contain("CHẠY CODE TUỲ Ý").And.Contain("BỊ CHẶN"));
            Assert.That(PolicyCli.Cli(_root, new[] { "mcp", "allow", "exec", "mcp__unity__execute_code" }), Is.EqualTo(0));
            Assert.That(McpPolicy.Classify("mcp__unity__execute_code", McpPolicy.Load(_root)), Is.EqualTo(McpKind.ExecAllowed));
        }

        [Test]
        public void McpAllow_QuotedPrefix_IsAsked_BadNamesAreNot()
        {
            AskAndGrant("./Tools/test-gate.sh mcp allow read 'mcp__unity__get_*'");
            Assert.That(DeniedReason("./Tools/test-gate.sh mcp allow read rm"), Does.Contain("not an MCP tool"));
            Assert.That(DeniedReason("./Tools/test-gate.sh mcp allow deny mcp__unity__x"), Does.Contain("usage"));
        }

        [Test]
        public void McpPolicy_WrittenByAnUnaskedCommand_IsStillTheOwners()
        {
            TaskStore.Save(_root, ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now"));
            Pre("echo x", "tu2");
            File.AppendAllText(Path.Combine(_root, "Tools/mcp-policy.txt"), "exec mcp__unity__execute_code\n");
            Assert.That(AgentHooks.Post(_root, Bash("echo x", "tu2"), _ => new List<string>()), Is.EqualTo(2));
        }
    }
}
