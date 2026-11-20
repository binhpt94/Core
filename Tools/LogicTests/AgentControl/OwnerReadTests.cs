using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// Reading the gate's output (Dashboard/Data/) asks the owner yes/no, and one yes = one read: the next read asks again.
    /// Other reads are untouched (no ask, no event); Bash can't go around it; .agent/ stays denied in settings.
    /// </summary>
    public sealed class OwnerReadTests
    {
        private const string Results = "Dashboard/Data/EditMode.xml";
        private string _root;

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private HookInput Tool(string tool, string key, string value, string id, string pattern = null)
        {
            var input = new Dictionary<string, object> { [key] = Path.Combine(_root, value) };
            if (pattern != null)
                input["pattern"] = pattern;
            return HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sess1", ["tool_name"] = tool, ["tool_use_id"] = id, ["tool_input"] = input
            }));
        }

        private (int code, string stdout) Pre(HookInput h)
        {
            var old = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { return (AgentHooks.Pre(_root, h), sw.ToString()); }
            finally { Console.SetOut(old); }
        }

        private static string Ask(string stdout)
        {
            var s = JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput");
            Assert.That(s.GetProperty("permissionDecision").GetString(), Is.EqualTo("ask"));
            return s.GetProperty("permissionDecisionReason").GetString();
        }

        private List<AgentEvent> Events(string type) => EventLog.For(_root).ReadAll().Where(e => e.Type == type).ToList();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ownerread-" + Guid.NewGuid().ToString("N"));
            Write(Results, "<test-run result=\"Failed\" />");
            Write("Docs/RULES.md", "rules\n");
            Recorder.Root = _root;
            Recorder.Session = "sess1";
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            Directory.Delete(_root, true);
        }

        [Test]
        public void ReadingTheGatesOutput_AsksTheOwner_EveryTime()
        {
            var (code, stdout) = Pre(Tool("Read", "file_path", Results, "r1"));
            Assert.That(code, Is.EqualTo(0), "ask = exit 0 + JSON on stdout");
            string reason = Ask(stdout);
            Assert.That(reason, Does.Contain(Results).And.Contain("MỘT LẦN").And.Contain("kết quả test Unity EditMode"));
            Assert.That(reason, Does.Not.Contain(OwnerAskText.GrantNote), "no grant: nothing to explain about a nonce");

            var again = Pre(Tool("Read", "file_path", Results, "r2"));
            Assert.That(again.code, Is.EqualTo(0));
            Ask(again.stdout);   // asked again: a yes never outlives its one read
            Assert.That(Events(OwnerRead.AskedEvent).Select(e => e.Get("toolUseId")), Is.EqualTo(new[] { "r1", "r2" }));
            Assert.That(Events(OwnerRead.AskedEvent)[0].Get("hash"), Is.Not.Null.And.Length.EqualTo(64));
        }

        [Test]
        public void GrepWithItsPath_IsAsked_AndShowsThePattern()
        {
            var (code, stdout) = Pre(Tool("Grep", "path", Results, "g1", "result=\"Failed\""));
            Assert.That(code, Is.EqualTo(0));
            Assert.That(Ask(stdout), Does.Contain("result=\"Failed\""));
        }

        [Test]
        public void AMissingFile_IsBlocked_WithoutAskingTheOwner()
        {
            var (code, stdout) = Pre(Tool("Read", "file_path", "Dashboard/Data/PlayMode.xml", "r1"));
            Assert.That(code, Is.EqualTo(2));
            Assert.That(stdout, Is.Empty);
            Assert.That(Events(OwnerRead.AskedEvent), Is.Empty);
        }

        [Test]
        public void OtherReads_AreUntouched()
        {
            Assert.That(Pre(Tool("Read", "file_path", "Docs/RULES.md", "r1")), Is.EqualTo((0, "")));
            Assert.That(Pre(Tool("Read", "file_path", "Dashboard/index.html", "r2")), Is.EqualTo((0, "")), "the static view is not the data");
            var grepAll = HookInput.Parse("{\"session_id\":\"sess1\",\"tool_name\":\"Grep\",\"tool_use_id\":\"g1\",\"tool_input\":{\"pattern\":\"x\"}}");
            Assert.That(Pre(grepAll), Is.EqualTo((0, "")), "no path: ripgrep skips the ignored Data/ folder");
            Assert.That(File.Exists(Path.Combine(_root, ".agent", "events.jsonl")), Is.False, "ordinary reads log nothing");
        }

        [Test]
        public void TheReadRan_IsRecordedAsTheOwnersYes()
        {
            Pre(Tool("Read", "file_path", Results, "r1"));
            AgentHooks.Post(_root, Tool("Read", "file_path", Results, "r1"), _ => new List<string>());
            var done = Events(OwnerRead.DoneEvent);
            Assert.That(done.Select(e => e.Get("toolUseId") + " " + e.Get("path")), Is.EqualTo(new[] { "r1 " + Results }));
        }

        [TestCase("cat Dashboard/Data/EditMode.xml")]
        [TestCase("grep -n Failed ./Dashboard/Data/EditMode.xml")]
        [TestCase("ls Dashboard/Data")]
        [TestCase("head -50 /home/x/Core/Dashboard/Data/EditMode.xml")]
        public void BashCantGoAroundTheAsk(string cmd) =>
            Assert.That(AgentHooks.ForbiddenCommand(cmd), Does.Contain("Read tool"));

        [TestCase("grep -rn \"Dashboard/Data\" Tools")]
        [TestCase("./Tools/run-unity-tests.sh EditMode")]
        [TestCase("./Tools/test-gate.sh files clean --regenerable")]
        public void MentionsAndTheGate_AreNotReads(string cmd) => Assert.That(AgentHooks.ForbiddenCommand(cmd), Is.Null);

        [Test]
        public void Settings_HookSeesReads_AndOnlyTheEventLogStaysDenied()
        {
            string root = Layout.FindRoot(TestContext.CurrentContext.TestDirectory);
            var s = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".claude", "settings.json"))).RootElement;
            var deny = s.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(deny, Does.Not.Contain("Read(Dashboard/Data/**)"), "a settings deny can't be overridden by the hook's ask");
            Assert.That(deny, Does.Contain("Read(.agent/**)"));
            var allow = s.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(allow.Any(a => a.Contains("Dashboard")), Is.False, "no standing permission: one yes = one read");
            var matchers = s.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray().Select(e => e.GetProperty("matcher").GetString());
            Assert.That(matchers.Any(m => m.Split('|').Contains("Read") && m.Split('|').Contains("Grep")), Is.True);
        }
    }
}
