using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Files;

namespace LogicTests.AgentControl
{
    /// <summary>Cleanup is deterministic and never deletes a human's work: untracked + agent-made + unchanged, or nothing.</summary>
    public sealed class FilesTests
    {
        private string _root;

        private void Git(string a)
        {
            var p = Process.Start(new ProcessStartInfo("git", a) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private static HookInput Bash(string session, string cmd) => HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["session_id"] = session, ["tool_name"] = "Bash", ["tool_use_id"] = "t" + cmd.Length,
            ["tool_input"] = new Dictionary<string, string> { ["command"] = cmd }
        }));

        /// <summary>An agent session writes files through Bash (observed by the git snapshot).</summary>
        private void AgentWrites(string session, params string[] paths)
        {
            var h = Bash(session, "write " + string.Join(" ", paths));
            AgentHooks.Pre(_root, h);
            foreach (var p in paths)
                Write(p, "made by " + session);
            AgentHooks.Post(_root, h, _ => new List<string>());
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "files-" + Guid.NewGuid().ToString("N"));
            Write("Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Git("init -q"); Git("add -A"); Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            TaskStore.Save(_root, TestGate.AgentControl.ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now"));

            // Session "crashed": wrote 3 files, never stopped cleanly. Then a newer session started.
            Recorder.Session = "crashed";
            AgentWrites("crashed", "Assets/_Game/Features/A/Assets/Private/New Scene.unity", "Assets/_Game/Features/A/Logic/Helper.cs", "Assets/_Game/Features/A/Logic/Touched.cs");
            Write("Assets/_Game/Features/A/Logic/Touched.cs", "the human edited it afterwards");
            Write("Assets/_Game/Features/A/Logic/Human.cs", "a human's new file");
            Recorder.Session = "current";
            Recorder.Emit(Recorder.New(EventTypes.SessionStarted, "hook"));
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        private LooseFile L(string name) => FilesReport.Loose(_root).Single(f => f.Path.EndsWith(name, StringComparison.Ordinal));

        [Test]
        public void Origins_AreDerivedFromTheEventLog_NotFromARegistry()
        {
            Assert.That(L("Helper.cs").Origin, Is.EqualTo("crashed/main"));
            Assert.That(L("Helper.cs").Orphan, Is.True, "its session never stopped cleanly and a newer one started");
            Assert.That(L("Helper.cs").SafeToDelete, Is.True);
            Assert.That(L("Touched.cs").SafeToDelete, Is.False, "changed after the agent wrote it");
            Assert.That(L("Human.cs").Origin, Is.EqualTo("unknown"));
            Assert.That(L("New Scene.unity").Problems, Has.Some.StartsWith("ARCH131"));

            Recorder.Session = "crashed2";
            AgentWrites("crashed2", "plan/inbox/proposal.md");
            Recorder.Session = "current";
            Recorder.Emit(Recorder.New(EventTypes.SessionStarted, "hook"));
            Assert.That(L("proposal.md").Orphan || L("proposal.md").SafeToDelete, Is.False, "a proposal waits for the human: never cleanup material");
        }

        [Test]
        public void WithoutEvidenceThatASessionEnded_NothingIsAnOrphan()
        {
            // Hooks installed mid-session / log deleted: no SESSION_STARTED after the writes. The running agent's files must stay.
            var events = EventLog.For(_root).ReadAll().Where(e => e.Type != EventTypes.SessionStarted).Select(EventLog.Serialize).ToList();
            File.Delete(EventLog.For(_root).Path);
            var log = EventLog.For(_root);
            foreach (var line in events)
            {
                var e = EventLog.Parse(line);
                log.Append(new AgentEvent { Type = e.Type, Source = e.Source, Session = e.Session, Agent = e.Agent, Data = e.Data });
            }
            Assert.That(FilesReport.Loose(_root).Any(f => f.Orphan), Is.False);
            FilesCli.Run(_root, new[] { "files", "clean", "--orphans", "--yes" });
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/Helper.cs")), Is.True);
        }

        [Test]
        public void CleanOrphans_DeletesOnlyWhatIsProvablySafe()
        {
            Assert.That(FilesCli.Run(_root, new[] { "files", "clean", "--orphans" }), Is.EqualTo(0));
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/Helper.cs")), Is.True, "dry run by default");

            FilesCli.Run(_root, new[] { "files", "clean", "--orphans", "--yes" });
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/Helper.cs")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Assets/Private/New Scene.unity")), Is.False);
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/Touched.cs")), Is.True, "human-touched: kept");
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/Human.cs")), Is.True, "unknown origin: kept");
            Assert.That(File.Exists(Path.Combine(_root, "Assets/_Game/Features/A/Logic/X.cs")), Is.True, "tracked: never touched");
            Assert.That(EventLog.For(_root).ReadAll().Count(e => e.Type == EventTypes.FileCleaned), Is.EqualTo(2));
            Assert.That(EventLog.For(_root).Verify().Ok, Is.True);
        }

        [Test]
        public void Stop_NamesTheJunkThisSessionCreated()
        {
            Recorder.Session = "s2";
            AgentWrites("s2", "Assets/_Game/Features/A/Logic/Arena.cs.orig");
            var findings = AgentHooks.StopChecks(_root, HookInput.Parse("{\"session_id\":\"s2\"}"), _ => null);
            Assert.That(findings, Has.Some.StartsWith("JUNK Assets/_Game/Features/A/Logic/Arena.cs.orig").And.Contains("rm \""));
            Assert.That(findings.Any(f => f.Contains("New Scene")), Is.False, "another session's leftovers are not this agent's to explain");
        }

        [Test]
        public void Unused_FollowsTheGuidGraphFromEntryPoints()
        {
            const string P = "Assets/_Game/Features/A/Assets/";
            Write(P + "Private/Used.prefab", "GameObject:\n  sprite: {fileID: 1, guid: " + new string('c', 32) + ", type: 3}\n");
            Write(P + "Private/Used.prefab.meta", "guid: " + new string('b', 32) + "\n");
            Write(P + "Private/used.png", "PNG");
            Write(P + "Private/used.png.meta", "guid: " + new string('c', 32) + "\n");
            Write(P + "Private/forgotten.png", "PNG-big-old");
            Write(P + "Private/forgotten.png.meta", "guid: " + new string('d', 32) + "\n");
            Write(P + "Private/Recipe.generated.prefab", "GameObject: {}\n");
            Write(P + "Private/Recipe.generated.prefab.meta", "guid: " + new string('e', 32) + "\n");
            Write(P + "Public/AEntry.asset", "MonoBehaviour:\n  view: {fileID: 1, guid: " + new string('b', 32) + ", type: 3}\n");
            Write(P + "Public/AEntry.asset.meta", "guid: " + new string('f', 32) + "\n");
            Write("Assets/_Game/Features/A/Editor/Recipe.cs", "class R { const string Path = \"" + P + "Private/Recipe.generated.prefab\"; }");

            var unused = FilesReport.Unused(_root).Select(u => u.Path).ToList();
            Assert.That(unused, Is.EqualTo(new[] { P + "Private/forgotten.png" }), "used.png is reached through Used.prefab; the recipe output through code");
        }
    }
}
