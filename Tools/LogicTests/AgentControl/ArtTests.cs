using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>Art level is computed from files (binding YAML, bound art, human approvals by content hash, art gate results).</summary>
    public sealed class ArtStatusTests
    {
        private string _root;
        private const string SlotGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PrefabGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", SpriteGuid = "cccccccccccccccccccccccccccccccc";

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "art-" + Guid.NewGuid().ToString("N"));
            Write("UnityProject/Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs", "class AArtSlots { object S = new ArtSlotContract(\"A.Hero\", k); object T = new ArtSlotContract(\"A.Side\", k); }");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/Hero.prefab", "--- !u!1 &1\nGameObject:\n  sprite: {fileID: 21300000, guid: " + SpriteGuid + ", type: 3}\n");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/Hero.prefab.meta", "guid: " + PrefabGuid + "\n");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-1");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png.meta", "guid: " + SpriteGuid + "\n");
            Bind("candidate");
        }

        private void Bind(string field) =>
            Write("UnityProject/Assets/_Game/Features/A/Assets/Public/HeroArt.asset", "MonoBehaviour:\n  slotId: A.Hero\n  " + field + ": {fileID: 100100000, guid: " + PrefabGuid + ", type: 3}\n");

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private SlotState Hero() => ArtStatus.Compute(_root)["A.Hero"];

        [Test]
        public void Unbound_IsGraybox_Bound_IsCandidate()
        {
            Assert.That(ArtStatus.Compute(_root)["A.Side"].Level, Is.EqualTo(ArtLevels.Graybox));
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate));
            Assert.That(Hero().BoundFile, Is.EqualTo("UnityProject/Assets/_Game/Features/A/Assets/Private/Hero.prefab"));
        }

        [Test]
        public void Approval_IsForThisContent_ChangingTheSpriteMakesItStale()
        {
            ArtStatus.Accept(_root, Hero());
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Approved));
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-2"); // the artist repaints the sprite the prefab uses
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Stale));
        }

        [Test]
        public void GoingBackToApprovedContent_IsApprovedAgain_EveryApprovalIsKept()
        {
            ArtStatus.Accept(_root, Hero());
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-2");
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Stale));
            ArtStatus.Accept(_root, Hero()); // the new version is approved too
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-1"); // roll back to the first one
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Approved), "an approval is for content, not for the latest accept");
            Assert.That(ArtStatus.LoadAccepted(_root)["A.Hero"].Count, Is.EqualTo(2));
        }

        [Test]
        public void Final_NeedsApproval_FinalBinding_AndAPassingArtGate()
        {
            Bind("final");
            ArtStatus.Accept(_root, Hero());
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Approved), "no art gate result yet");
            Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 48x80 pivot 50%,50%\n");
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Final));
            Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 120x80 pivot 50%,50%\nA-SIZE width 120 > max 72\n");
            Assert.That(Hero().Gate, Is.EqualTo("failed"));
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Approved));
        }

        [Test]
        public void ChangeImpact_ArtFilesMapToTheirSlot()
        {
            var impact = new ChangeImpact(_root);
            foreach (var p in new[] { "UnityProject/Assets/_Game/Features/A/Assets/Public/HeroArt.asset", "UnityProject/Assets/_Game/Features/A/Assets/Private/Hero.prefab", "UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png" })
            {
                var i = impact.Classify(p);
                Assert.That(i.Roles, Is.EqualTo(new[] { ChangeRole.Art }), i.ToString());
                Assert.That(i.Slots, Is.EqualTo(new[] { "A.Hero" }), i.ToString());
            }
            var catalog = impact.Classify("UnityProject/Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs");
            Assert.That(catalog.Roles, Is.EquivalentTo(new[] { ChangeRole.Pure, ChangeRole.Art }));
            Assert.That(catalog.Slots, Is.EquivalentTo(new[] { "A.Hero", "A.Side" }));
        }
    }

    public sealed class PlanArtTests
    {
        private const string Plan =
            "## F-BOSS  Boss  [P0] [modules: Combat] [slots: C.Boss] [art: approved] [art-risk]\n" +
            "- R-FEAS  Art khả thi [feasibility]\n- R-LEARN  ≥ 2 attacks [story]\n- R-AI  boss AI\n";

        private static PlanInputs Inputs(string level)
        {
            var i = new PlanInputs { Plan = PlanParser.Parse(Plan) };
            i.Slots["C.Boss"] = new SlotState { Id = "C.Boss", Level = level, Detail = "d", Gate = "not run" };
            return i;
        }

        [Test]
        public void ArtRisk_BlocksGameplayUntilFeasibilityIsAccepted()
        {
            var r = PlanStatus.Compute(Inputs(ArtLevels.Graybox));
            var f = r.Feature("F-BOSS");
            Assert.That(f.BlockedBy, Has.Some.Contains("art feasibility R-FEAS"));
            Assert.That(PlanScheduler.Next(r).Single().Lane, Is.EqualTo("blocked"));

            var accepted = Inputs(ArtLevels.Graybox);
            accepted.Accepted["R-FEAS"] = accepted.Plan.Requirement("R-FEAS").Hash;
            Assert.That(PlanStatus.Compute(accepted).Feature("F-BOSS").BlockedBy, Is.Empty);
        }

        [Test]
        public void ArtBelowTheRequiredLevel_IsArtDebt_ForArtists_AndBlocksDone()
        {
            var r = PlanStatus.Compute(Inputs(ArtLevels.Candidate));
            var f = r.Feature("F-BOSS");
            Assert.That(f.ArtLevel, Is.EqualTo(ArtLevels.Candidate));
            Assert.That(f.ArtDebt.Single(), Does.StartWith("C.Boss: CANDIDATE, cần approved"));
            Assert.That(r.Gaps.Single(g => g.Kind == "ART_DEBT").Actor, Is.EqualTo("art"));
            Assert.That(PlanStatus.Compute(Inputs(ArtLevels.Approved)).Feature("F-BOSS").ArtDebt, Is.Empty);
        }

        [Test]
        public void Disciplines_AreCountedSeparately()
        {
            var f = PlanStatus.Compute(Inputs(ArtLevels.Graybox)).Feature("F-BOSS");
            Assert.That((f.StoryTotal, f.GameplayTotal, f.Total), Is.EqualTo((1, 1, 3)));
            Assert.That(PlanParser.Parse(Plan).Requirement("R-FEAS").IsManual, Is.True, "feasibility is a human decision");
        }

        [Test]
        public void ArtTags_AreParsedStrictly()
        {
            var p = PlanParser.Parse("## F-X  X [art: shiny] [slots: A.B, A.C]\n");
            Assert.That(p.Errors.Single(), Does.Contain("[art: shiny] must be one of graybox, candidate, approved, final"));
            Assert.That(p.Feature("F-X").Slots, Is.EqualTo(new[] { "A.B", "A.C" }));
        }
    }
}
