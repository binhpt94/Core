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
    /// <summary>features.md: an AI may write it only through Edit/Write, each write checked by the gate and confirmed by the owner (yes/no).</summary>
    public sealed class PlanEditGateTests
    {
        private const string Plan = "# Plan: T\n\n## F-A  Feature A  [P1] [modules: A]\n- R-A-ONE  Một\n";
        private string _root;
        private string PlanPath => Path.Combine(_root, PlanParser.RelativePath);

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

        private static HookInput Tool(string tool, string id, Dictionary<string, object> input) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["session_id"] = "sess1", ["tool_name"] = tool, ["tool_use_id"] = id, ["tool_input"] = input }));

        private HookInput EditPlan(string oldText, string newText, string id = "tu1") =>
            Tool("Edit", id, new Dictionary<string, object> { ["file_path"] = PlanPath, ["old_string"] = oldText, ["new_string"] = newText });

        private static List<string> NoRules(IEnumerable<string> files) => new List<string>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "planedit-" + Guid.NewGuid().ToString("N"));
            Write("UnityProject/Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Write(PlanParser.RelativePath, Plan);
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            Recorder.Session = "sess1";
            TaskStore.Save(_root, ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now"));
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        [Test]
        public void AValidEdit_AsksTheOwner_WithTheRequirementChanges()
        {
            var d = PlanEditGate.Decide(_root, EditPlan("- R-A-ONE  Một\n", "- R-A-ONE  Một (sửa)\n- R-A-TWO  Hai  [manual]\n"));
            Assert.That(d.Kind, Is.EqualTo(PlanEditDecision.Ask));
            Assert.That(d.Reason, Does.Contain("+ R-A-TWO (F-A)  Hai  [manual]"));
            Assert.That(d.Reason, Does.Contain("~ R-A-ONE"));
            Assert.That(AgentHooks.Pre(_root, EditPlan("- R-A-ONE  Một\n", "- R-A-ONE  Một\n- R-A-TWO  Hai\n")), Is.EqualTo(0), "ask = exit 0 + JSON on stdout");
            Assert.That(EventLog.For(_root).ReadAll().Any(e => e.Type == PlanEditGate.AskedEvent), Is.True);
        }

        [Test]
        public void AnEditThatBreaksThePlan_IsBlocked_WithoutAsking()
        {
            var dup = PlanEditGate.Decide(_root, EditPlan("- R-A-ONE  Một\n", "- R-A-ONE  Một\n- R-A-ONE  Lặp\n"));
            Assert.That(dup.Kind, Is.EqualTo(PlanEditDecision.Deny));
            Assert.That(dup.Reason, Does.Contain("R-A-ONE"));
            var module = PlanEditGate.Decide(_root, EditPlan("[modules: A]", "[modules: Nope]"));
            Assert.That(module.Kind, Is.EqualTo(PlanEditDecision.Deny));
            Assert.That(module.Reason, Does.Contain("Nope"));
            Assert.That(AgentHooks.Pre(_root, EditPlan("[modules: A]", "[modules: Nope]")), Is.EqualTo(2));
        }

        [Test]
        public void OnlyEditAndWrite_MayTouchThePlan()
        {
            var multi = Tool("MultiEdit", "tu9", new Dictionary<string, object> { ["file_path"] = PlanPath });
            Assert.That(PlanEditGate.Decide(_root, multi).Kind, Is.EqualTo(PlanEditDecision.Deny));
        }

        [Test]
        public void AnApprovedWrite_IsInScope_ALaterBashChangeIsNot()
        {
            string proposed = Plan + "- R-A-TWO  Hai\n";
            var write = Tool("Write", "tu2", new Dictionary<string, object> { ["file_path"] = PlanPath, ["content"] = proposed });
            Assert.That(AgentHooks.Pre(_root, write), Is.EqualTo(0));
            File.WriteAllText(PlanPath, proposed);                         // the owner said yes: Claude Code ran the Write
            Assert.That(AgentHooks.Post(_root, write, NoRules), Is.EqualTo(0), "approved: no SCOPE feedback");
            Assert.That(AgentHooks.StopChecks(_root, Tool("Stop", "s", new Dictionary<string, object>()), _ => null).Where(f => f.Contains("features.md")), Is.Empty);

            var bash = Tool("Bash", "tu3", new Dictionary<string, object> { ["command"] = "sed -i s/Hai/Ba/ Docs/Plan/features.md" });
            AgentHooks.Pre(_root, bash);
            File.WriteAllText(PlanPath, proposed.Replace("Hai", "Ba"));
            Assert.That(AgentHooks.Post(_root, bash, NoRules), Is.EqualTo(2), "every write asks again: Bash never does");
        }

        [Test]
        public void ARefusedWrite_GivesNoPermission_EvenForTheSameText()
        {
            string proposed = Plan + "- R-A-TWO  Hai\n";
            AgentHooks.Pre(_root, Tool("Write", "tu4", new Dictionary<string, object> { ["file_path"] = PlanPath, ["content"] = proposed }));
            // The owner said no: the Write never ran, so no PostToolUse. The same text through Bash is still a breach.
            var bash = Tool("Bash", "tu5", new Dictionary<string, object> { ["command"] = "cp draft Docs/Plan/features.md" });
            AgentHooks.Pre(_root, bash);
            File.WriteAllText(PlanPath, proposed);
            Assert.That(AgentHooks.Post(_root, bash, NoRules), Is.EqualTo(2));
        }

        private string CorePath => Path.Combine(_root, "Docs", "Plan", "Core.md");

        [Test]
        public void ACoreEdit_AsksTheOwner_WithTheLinesThatChange()
        {
            Write("Docs/Plan/Core.md", "# Game\n\nĐúng thì hạ slime.\nSai thì slime nổi điên.\n");
            var edit = Tool("Edit", "tc1", new Dictionary<string, object> { ["file_path"] = CorePath, ["old_string"] = "Sai thì slime nổi điên.", ["new_string"] = "Sai thì mất máu." });
            var d = PlanEditGate.Decide(_root, edit);
            Assert.That(d.Kind, Is.EqualTo(PlanEditDecision.Ask));
            Assert.That(d.Reason, Does.Contain("Docs/Plan/Core.md"));
            Assert.That(d.Reason, Does.Contain("+ Sai thì mất máu."));
            Assert.That(d.Reason, Does.Contain("- Sai thì slime nổi điên."));
            Assert.That(d.Reason, Does.Not.Contain("Đúng thì hạ slime."), "unchanged lines are not listed");
        }

        [Test]
        public void AnApprovedCoreWrite_IsInScope_ALaterBashChangeIsNot()
        {
            Write("Docs/Plan/Core.md", "# Game\n");
            var write = Tool("Write", "tc2", new Dictionary<string, object> { ["file_path"] = CorePath, ["content"] = "# Game\n\nMới.\n" });
            Assert.That(AgentHooks.Pre(_root, write), Is.EqualTo(0));
            File.WriteAllText(CorePath, "# Game\n\nMới.\n");
            Assert.That(AgentHooks.Post(_root, write, NoRules), Is.EqualTo(0));

            var bash = Tool("Bash", "tc3", new Dictionary<string, object> { ["command"] = "echo x >> Docs/Plan/Core.md" });
            AgentHooks.Pre(_root, bash);
            File.AppendAllText(CorePath, "x\n");
            Assert.That(AgentHooks.Post(_root, bash, NoRules), Is.EqualTo(2));
        }

        [Test]
        public void AnApproval_CountsOnlyForItsOwnFile()
        {
            // The plan approved with some content; the same content written to Core.md through Bash is still a breach.
            string proposed = Plan + "- R-A-TWO  Hai\n";
            var write = Tool("Write", "tc4", new Dictionary<string, object> { ["file_path"] = PlanPath, ["content"] = proposed });
            AgentHooks.Pre(_root, write);
            File.WriteAllText(PlanPath, proposed);
            AgentHooks.Post(_root, write, NoRules);
            var bash = Tool("Bash", "tc5", new Dictionary<string, object> { ["command"] = "cp Docs/Plan/features.md Docs/Plan/Core.md" });
            AgentHooks.Pre(_root, bash);
            File.WriteAllText(CorePath, proposed);
            Assert.That(AgentHooks.Post(_root, bash, NoRules), Is.EqualTo(2));
        }

        [Test]
        public void Draft_IsCheckedByTheGate_AndListsEveryChange()
        {
            var ok = PlanEditGate.Draft(_root, "# Plan: T\n\n## F-A  Feature A  [P0] [modules: A]\n- R-A-ONE  Một nghĩa mới\n\n## F-B  Feature B  [modules: A]\n- R-B-ONE  Bê\n");
            Assert.That(ok.Errors, Is.Empty);
            Assert.That(ok.Changes, Has.Some.StartsWith("~ F-A").And.Some.Contains("P1 → P0"));
            Assert.That(ok.Changes, Has.Some.StartsWith("+ F-B"));
            Assert.That(ok.Changes, Has.Some.StartsWith("+ R-B-ONE (F-B)"));
            Assert.That(ok.Changes, Has.Some.StartsWith("~ R-A-ONE").And.Some.Contains("Một nghĩa mới"));

            var removed = PlanEditGate.Draft(_root, "# Plan: T\n\n## F-A  Feature A  [P1] [modules: A]\n- R-A-TWO  Hai\n");
            Assert.That(removed.Changes, Has.Some.StartsWith("- R-A-ONE (F-A)"));

            var bad = PlanEditGate.Draft(_root, "# Plan: T\n\n## F-A  Feature A  [modules: Nope]\n- R-A-ONE  Một\n");
            Assert.That(bad.Errors, Has.Some.Contains("Nope"));
        }
    }
}
