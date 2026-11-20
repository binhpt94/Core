using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>Art versions: immutable files + a text log; "use" picks what the game shows per tier.</summary>
    public sealed class ArtVersionTests
    {
        private const string Slot = "A.Hero";
        private string _root, _src;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "artver-" + Guid.NewGuid().ToString("N"));
            _src = Path.Combine(_root, "src");
            Directory.CreateDirectory(_src);
            File.WriteAllText(Path.Combine(_src, "a.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(_src, "b.png"), "PNG-B");
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private static string FakeRender(string svg, string png)
        {
            File.WriteAllText(png, "PNG-from-" + Path.GetFileName(svg));
            return null;
        }

        private string Dir => Path.Combine(_root, ArtVersions.Dir(Slot));

        [Test]
        public void Add_NumbersVersions_KeepsSvgAndPng_NeverOverwrites_AndSwitchesTheTier()
        {
            var v1 = ArtVersions.Add(_root, Slot, Path.Combine(_src, "a.svg"), "candidate", "claude", "first", null, "2026-11-21", FakeRender);
            var v2 = ArtVersions.Add(_root, Slot, Path.Combine(_src, "b.png"), "final", "codex", "from v001", "v001", "2026-11-22", FakeRender);
            var v3 = ArtVersions.Add(_root, Slot, Path.Combine(_src, "a.svg"), "candidate", "claude", "", "v001", "2026-11-22", FakeRender, keepUse: true);
            Assert.That(new[] { v1.Id, v2.Id, v3.Id }, Is.EqualTo(new[] { "v001", "v002", "v003" }));
            Assert.That(File.ReadAllText(Path.Combine(Dir, "v001.png")), Is.EqualTo("PNG-from-v001.svg"));
            Assert.That(File.Exists(Path.Combine(Dir, "v001.svg")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(Dir, "v002.png")), Is.EqualTo("PNG-B"));
            var log = ArtVersions.Load(_root, Slot);
            Assert.That(log.Use["candidate"], Is.EqualTo("v001"), "--keep-use leaves the tier where it was");
            Assert.That(log.Use["final"], Is.EqualTo("v002"));
            Assert.That(log.Find("v002").Parent, Is.EqualTo("v001"));
            Assert.That(log.Find("v002").Note, Is.EqualTo("from v001"));
        }

        [Test]
        public void Use_SwitchesAndRollsBack_UnknownVersionIsRefused()
        {
            ArtVersions.Add(_root, Slot, Path.Combine(_src, "b.png"), "candidate", "claude", "", null, "d", FakeRender);
            ArtVersions.Add(_root, Slot, Path.Combine(_src, "b.png"), "candidate", "claude", "", null, "d", FakeRender);
            Assert.That(ArtVersions.Use(_root, Slot, "v001", null), Is.EqualTo("v002"));
            Assert.That(ArtVersions.InUse(_root, Slot, "candidate"), Is.EqualTo("v001"));
            ArtVersions.Use(_root, Slot, "v002", "final"); // a candidate can be promoted to the final tier as is
            Assert.That(ArtVersions.InUse(_root, Slot, "final"), Is.EqualTo("v002"));
            Assert.Throws<ArgumentException>(() => ArtVersions.Use(_root, Slot, "v009", null));
        }

        [Test]
        public void FailedRender_LeavesNoVersionBehind()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ArtVersions.Add(_root, Slot, Path.Combine(_src, "a.svg"), "candidate", "claude", "", null, "d", (s, p) => "boom"));
            Assert.That(ArtVersions.Load(_root, Slot).Versions, Is.Empty);
            Assert.That(Directory.GetFiles(Dir), Is.Empty);
        }

        [Test]
        public void Log_RoundTrips_WithQuotesFlattened()
        {
            var log = ArtVersionLog.Parse("v001 2026-11-21 candidate claude - \"a \\\"b\\\" c\"\nuse candidate v001\n");
            var again = ArtVersionLog.Parse(log.Format(Slot));
            Assert.That(again.Find("v001").Tier, Is.EqualTo("candidate"));
            Assert.That(again.Use["candidate"], Is.EqualTo("v001"));
            Assert.That(again.Format(Slot), Is.EqualTo(log.Format(Slot)));
        }

        [Test]
        public void Codex_RunsInAScratchFolder_WithTheReferenceAttached_AndAsksForImageGen()
        {
            var args = ArtCodexRun.Args("/w/", "/r/v001.png");
            Assert.That(args.Take(6), Is.EqualTo(new[] { "exec", "-C", "/w/", "-s", "workspace-write", "--skip-git-repo-check" }));
            Assert.That(args, Does.Contain("-i"));
            Assert.That(args[args.IndexOf("-i") + 1], Is.EqualTo("/r/v001.png"));
            Assert.That(args.Last(), Is.EqualTo("-"), "prompt on stdin");
            var spec = new ArtSlotSpec { Id = Slot, Kind = "UiImage", MinWidth = 10, MaxWidth = 20, MinHeight = 10, MaxHeight = 20, PivotXPercent = 50, PivotYPercent = 50, TickRate = 20 };
            string prompt = ArtCodexRun.Prompt(spec, "", "", "v001");
            Assert.That(prompt, Does.Contain("image_gen"));
            Assert.That(prompt, Does.Contain("version v001"));
            Assert.That(prompt, Does.Contain("./final.png"));
            Assert.That(prompt, Does.Contain("transparent background"), "the brief prompt follows");
        }

        [Test]
        public void Inkscape_RendersAtTheViewBoxSize_WhenInstalled()
        {
            string svg = Path.Combine(_src, "sq.svg"), png = Path.Combine(_src, "sq.png");
            File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 60\" width=\"40\" height=\"60\"><rect x=\"5\" y=\"5\" width=\"30\" height=\"50\" fill=\"#3a3\"/></svg>");
            string error = ArtVersionCli.Inkscape(svg, png);
            if (error != null && error.StartsWith("inkscape not found", StringComparison.Ordinal))
                Assert.Pass("inkscape not installed here; the CLI reports it");
            Assert.That(error, Is.Null);
            var bytes = File.ReadAllBytes(png);
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19], h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Assert.That(new[] { w, h }, Is.EqualTo(new[] { 40, 60 }), "PNG IHDR size = viewBox");
        }
    }
}
