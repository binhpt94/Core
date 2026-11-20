using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// The dashboard's multi-slot acceptance: the owner ticks finals, saves a draft, an AI runs `art accept --draft FILE`; the gate
    /// recomputes every line from the real art and asks yes/no once for exactly what still matches.
    /// </summary>
    public sealed class ArtAcceptDraftTests
    {
        private string _root;
        private const string HeroPrefab = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1", SidePrefab = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2";
        private const string Private = "UnityProject/Assets/_Game/Features/A/Assets/Private/";

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private void Bind(string name, string guid, string tier)
        {
            Write(Private + name + "." + tier + ".prefab", "--- !u!1 &1\nGameObject:\n  m_Name: " + name + "\n");
            Write(Private + name + "." + tier + ".prefab.meta", "guid: " + guid + "\n");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Public/" + name + "Art.asset",
                  "MonoBehaviour:\n  slotId: A." + name + "\n  " + tier + ": {fileID: 100100000, guid: " + guid + ", type: 3}\n");
            Write("Dashboard/Data/art/A." + name + "." + tier + ".txt", "A." + name + " [" + tier + "] 48x80 pivot 50%,50%\n"); // art gate passed
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "artdraft-" + Guid.NewGuid().ToString("N"));
            Write("UnityProject/Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs",
                  "class AArtSlots { object S = new ArtSlotContract(\"A.Hero\", k); object T = new ArtSlotContract(\"A.Side\", k); object U = new ArtSlotContract(\"A.Gray\", k); }");
            Bind("Hero", HeroPrefab, "final");
            Bind("Side", SidePrefab, "candidate");
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private SlotState Slot(string id) => ArtStatus.Compute(_root)[id];
        private string DraftLine(string id) { var s = Slot(id); return id + " " + (s.Version ?? "-") + " " + s.Hash; }
        private string SaveDraft(params string[] lines)
        {
            string path = Path.Combine(_root, ".scratch", "art-accept.draft.txt");
            Write(".scratch/art-accept.draft.txt", "# art accept draft · saved 2026-11-20T10:00:00Z\n" + string.Join("\n", lines) + "\n");
            return path;
        }

        [Test]
        public void AMatchingFinal_IsAccepted_ACandidateIsLeftOutWithItsReason()
        {
            var r = ArtAcceptDraft.Parse("# x\n" + DraftLine("A.Hero") + "\n" + DraftLine("A.Side") + "\n").Evaluate(ArtStatus.Compute(_root));
            Assert.That(r.Accept.Select(s => s.Id), Is.EqualTo(new[] { "A.Hero" }));
            Assert.That(r.Rejected.Single().Slot, Is.EqualTo("A.Side"));
            Assert.That(r.Rejected.Single().Reason, Does.Contain("candidates need only the automatic art gate"));
        }

        [Test]
        public void ArtChangedAfterTheOwnerLooked_IsLeftOut()
        {
            string line = DraftLine("A.Hero");
            Write(Private + "Hero.final.prefab", "--- !u!1 &1\nGameObject:\n  m_Name: Hero repainted\n");
            Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 48x80 pivot 50%,50%\n"); // the gate passed on the new art too
            var r = ArtAcceptDraft.Parse(line).Evaluate(ArtStatus.Compute(_root));
            Assert.That(r.Accept, Is.Empty);
            Assert.That(r.Rejected.Single().Reason, Does.Contain("changed after you looked"));
        }

        [Test]
        public void AlreadyAccepted_IsSkipped_DuplicatesKeepTheLastLine_BadLinesAreErrors()
        {
            ArtStatus.Accept(_root, Slot("A.Hero"));
            var r = ArtAcceptDraft.Parse("A.Hero v009 nope\n" + DraftLine("A.Hero")).Evaluate(ArtStatus.Compute(_root));
            Assert.That(r.Accept, Is.Empty);
            Assert.That(r.Skipped.Single().Slot, Is.EqualTo("A.Hero"));
            Assert.That(r.Notes.Single(), Does.Contain("line 2 (the last one)"));

            var bad = ArtAcceptDraft.Parse("# c\nA.Hero v001\nnot a line at all here\n");
            Assert.That(bad.Errors, Has.Count.EqualTo(2));
            Assert.That(bad.Errors[0], Does.StartWith("line 2:"));
        }

        [Test]
        public void TheOwnerIsAskedOnce_ForExactlyWhatStillMatches_AndTheYesIsBoundToIt()
        {
            string file = SaveDraft(DraftLine("A.Hero"), DraftLine("A.Side"));
            var d = OwnerAsk.Decide(_root, "./Tools/test-gate.sh art accept --draft \"" + file + "\"");
            Assert.That(d.Kind, Is.EqualTo(OwnerAskDecision.Ask));
            Assert.That(d.Reason, Does.Contain("Duyệt 1 bản art"));
            Assert.That(d.Reason, Does.Contain("✓ A.Hero"));
            Assert.That(d.Reason, Does.Contain("✗ A.Side"));
            Assert.That(d.Bind, Is.EqualTo(ArtAcceptDraftCli.Evaluate(_root, file).result.Bind));

            Write(Private + "Hero.final.prefab", "--- !u!1 &1\nGameObject:\n  m_Name: Hero v2\n");
            Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 48x80 pivot 50%,50%\n");
            var again = OwnerAsk.Decide(_root, "./Tools/test-gate.sh art accept --draft \"" + file + "\"");
            Assert.That(again.Kind, Is.EqualTo(OwnerAskDecision.Deny), "nothing matches any more: the owner is not asked");
        }

        [Test]
        public void ADraftWithNothingToAccept_DoesNotAskTheOwner()
        {
            string file = SaveDraft(DraftLine("A.Side"));
            Assert.That(OwnerAsk.Decide(_root, "./Tools/test-gate.sh art accept --draft \"" + file + "\"").Kind, Is.EqualTo(OwnerAskDecision.Deny));
            Assert.That(OwnerAsk.Decide(_root, "./Tools/test-gate.sh art accept --draft \"" + Path.Combine(_root, "missing.txt") + "\"").Kind, Is.EqualTo(OwnerAskDecision.Deny));
            Assert.That(AgentHooks.ForbiddenCommand("dotnet run --project Tools/TestGate -- art accept --draft x.txt"), Is.Not.Null, "still an owner command");
        }

        [Test]
        public void TheArtTab_ShowsModuleFiles_AndOnlyTicksFinalsThatPassedTheGate()
        {
            var views = ArtSlotView.Build(_root, ArtStatus.Compute(_root).Values, new FeatureState[0]).ToDictionary(v => v.Id);
            Assert.That(views["A.Hero"].Module, Is.EqualTo("A"));
            Assert.That(views["A.Hero"].File, Is.EqualTo("Assets/_Game/Features/A/Assets/Private/Hero.final.prefab"), "Unity-relative path");
            Assert.That(views["A.Hero"].Contract, Is.EqualTo("Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs"));
            Assert.That((views["A.Hero"].Acceptable, views["A.Hero"].Refusal), Is.EqualTo((true, "")));
            Assert.That(views["A.Side"].Acceptable, Is.False);
            Assert.That(views["A.Side"].Refusal, Does.StartWith("Bản thử"));
            Assert.That(views["A.Gray"].Refusal, Does.StartWith("Chưa có art"));
        }

        [Test]
        public void TheArtTabReadsOnlyFieldsTheSlotDataHas()
        {
            string js = null;
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && js == null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Dashboard", "app.js")))
                    js = File.ReadAllText(Path.Combine(dir.FullName, "Dashboard", "app.js"));
            Assert.That(js, Is.Not.Null, "Dashboard/app.js not found");
            js = Regex.Replace(js, @"^\s*//.*$", "", RegexOptions.Multiline);
            var shape = JsonDocument.Parse(AgentCli.ToJson(new ArtSlotView { Box = new ArtSlotView.ArtBox() })).RootElement;
            var reads = Regex.Matches(js, @"\bA\.([A-Za-z_]\w*)").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.That(reads.Where(f => !shape.TryGetProperty(f, out _)), Is.Empty, "app.js reads art slot fields the data doesn't have");
            Assert.That(reads.Count, Is.GreaterThan(10), "the check is really looking at the art tab code");
        }
    }
}
