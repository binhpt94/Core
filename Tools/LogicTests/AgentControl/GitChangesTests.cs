using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    public sealed class GitChangesTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "gitchanges-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Git("init -q");
            Git("config core.autocrlf true");                   // Windows default: LF files make git print a CRLF warning on stderr
            File.WriteAllText(Path.Combine(_root, "a.txt"), "one\n");
            Git("add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        private void Git(string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }

        [Test]
        public void GitWarnings_OnStderr_AreNotMistakenForChangedFiles()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo\n");
            File.WriteAllText(Path.Combine(_root, "b.txt"), "new\n");

            var files = GitChanges.Files(_root, "HEAD", out string fullReason);

            Assert.That(fullReason, Is.Null);
            Assert.That(files, Is.EqualTo(new[] { "a.txt", "b.txt" }), "a 'LF will be replaced by CRLF' warning would make the plan FULL (unknown file role)");
            Assert.That(GitChanges.Staged(_root), Is.Empty);
        }
    }
}
