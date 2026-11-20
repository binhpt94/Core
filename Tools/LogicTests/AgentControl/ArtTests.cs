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

        private void GatePassed() => Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 48x80 pivot 50%,50%\n");

        [Test]
        public void Candidate_NeedsNoAcceptance_ANewVersionStaysCandidate()
        {
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate));
            Assert.That(ArtCli.Refusal(Hero()), Does.Contain("candidates need only the automatic art gate"));
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-2"); // the artist adds v002
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate), "a new candidate is never STALE: nobody has to look again");
            Assert.That(Hero().Detail, Does.Contain("only the automatic art gate"));
        }

        [Test]
        public void FinalAcceptance_IsForThisContent_ChangingTheSpriteMakesItStale()
        {
            Bind("final");
            GatePassed();
            ArtStatus.Accept(_root, Hero());
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Final));
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-2"); // the artist repaints the sprite the prefab uses
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Stale));
        }

        [Test]
        public void GoingBackToAcceptedFinal_IsFinalAgain_EveryAcceptanceIsKept()
        {
            Bind("final");
            GatePassed();
            ArtStatus.Accept(_root, Hero());
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-2");
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Stale));
            ArtStatus.Accept(_root, Hero()); // the new final is accepted too
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/hero.png", "PNG-1"); // roll back to the first one
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Final), "an acceptance is for content, not for the latest accept");
            Assert.That(ArtStatus.LoadAccepted(_root)["A.Hero"].Count, Is.EqualTo(2));
        }

        [Test]
        public void Final_NeedsAPassingArtGate_ThenTheOwnersAcceptance()
        {
            Bind("final");
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate), "final bound, not accepted");
            Assert.That(ArtCli.Refusal(Hero()), Does.Contain("has not run"), "the owner accepts only what the machine passed");
            GatePassed();
            Assert.That(ArtCli.Refusal(Hero()), Is.Null);
            ArtStatus.Accept(_root, Hero());
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Final));
            Write("Dashboard/Data/art/A.Hero.final.txt", "A.Hero [final] 120x80 pivot 50%,50%\nA-SIZE width 120 > max 72\n");
            Assert.That(Hero().Gate, Is.EqualTo("failed"));
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate));
            Assert.That(ArtCli.Refusal(Hero()), Does.Contain("FAILED"));
        }

        [Test]
        public void OldCandidateAcceptances_AreHistory_TheyNeverMakeAFinalStale()
        {
            Write("Docs/Plan/art-accepted.txt", "A.Hero 0123456789ab 2026-11-20 UnityProject/Assets/_Game/Features/A/Assets/Private/Art/Hero.candidate.prefab v001\n");
            Assert.That(ArtStatus.LoadAccepted(_root).ContainsKey("A.Hero"), Is.False);
            Bind("final");
            Assert.That(Hero().Level, Is.EqualTo(ArtLevels.Candidate), "waiting for acceptance, not STALE");
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
                Assert.That(i.ArtContent, Is.True, "changing art: its art gate blocks");
            }
            var catalog = impact.Classify("UnityProject/Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs");
            Assert.That(catalog.Roles, Is.EquivalentTo(new[] { ChangeRole.Pure, ChangeRole.Art }));
            Assert.That(catalog.Slots, Is.EquivalentTo(new[] { "A.Hero", "A.Side" }));
            Assert.That(catalog.ArtContent, Is.False, "a contract change (dev) only records art debt: art never blocks dev");
        }
    }

    public sealed class PlanArtTests
    {
        private const string Plan =
            "## F-BOSS  Boss  [P0] [modules: Combat] [slots: C.Boss] [art: final] [art-risk]\n" +
            "- R-FEAS  Art khả thi [feasibility]\n- R-LEARN  ≥ 2 attacks [story]\n- R-AI  boss AI\n";

        // F-SKIN is gameplay-done (its only requirement is accepted) but its art is far from final; F-AFTER needs it hard.
        private const string DonePlan =
            "## F-SKIN  Skin  [P0] [modules: Skin] [slots: C.Boss] [art: final]\n- R-SKIN  plays well [manual]\n" +
            "## F-AFTER  After  [P1] [modules: After]\n- R-AFTER  next thing [manual]\ndeps: hard F-SKIN\n";

        private static PlanInputs Inputs(string level, string plan = Plan, string gate = "not run")
        {
            var i = new PlanInputs { Plan = PlanParser.Parse(plan) };
            i.Slots["C.Boss"] = new SlotState { Id = "C.Boss", Level = level, Detail = "d", Gate = gate };
            return i;
        }

        [Test]
        public void ArtRisk_IsAWarningForTheOwner_NeverABlockForGameplay()
        {
            var r = PlanStatus.Compute(Inputs(ArtLevels.Graybox));
            var f = r.Feature("F-BOSS");
            Assert.That(f.BlockedBy, Is.Empty);
            Assert.That(f.ArtRisk, Has.Some.Contains("art feasibility R-FEAS"));
            Assert.That(r.Gaps.Single(g => g.Kind == "ART_RISK").Actor, Is.EqualTo("art"));
            Assert.That(PlanScheduler.Next(r).Select(x => x.Lane), Has.None.EqualTo("blocked"));

            var accepted = Inputs(ArtLevels.Graybox);
            accepted.Accepted["R-FEAS"] = accepted.Plan.Requirement("R-FEAS").Hash;
            Assert.That(PlanStatus.Compute(accepted).Feature("F-BOSS").ArtRisk, Is.Empty);
        }

        [Test]
        public void ArtBelowTheRequiredLevel_IsArtDebt_ForArtists_AndDoesNotBlockDone()
        {
            var r = PlanStatus.Compute(Inputs(ArtLevels.Candidate));
            var f = r.Feature("F-BOSS");
            Assert.That(f.ArtLevel, Is.EqualTo(ArtLevels.Candidate));
            Assert.That(f.ArtDebt.Single(), Does.StartWith("C.Boss: CANDIDATE, cần final"));
            Assert.That(f.ArtComplete, Is.False);
            Assert.That(r.Gaps.Single(g => g.Kind == "ART_DEBT").Actor, Is.EqualTo("art"));
            Assert.That(PlanStatus.Compute(Inputs(ArtLevels.Final)).Feature("F-BOSS").ArtDebt, Is.Empty);

            var done = Inputs(ArtLevels.Graybox, DonePlan, gate: "failed");
            done.Accepted["R-SKIN"] = done.Plan.Requirement("R-SKIN").Hash;
            var d = PlanStatus.Compute(done);
            Assert.That(d.Feature("F-SKIN").ArtDebt, Has.Count.EqualTo(1));
            Assert.That(d.Feature("F-SKIN").Status, Is.EqualTo(FeatureStatus.Done), "gameplay is done; art debt is tracked apart");
            Assert.That(d.Feature("F-AFTER").BlockedBy, Is.Empty, "a hard dependency never waits for art");
        }

        [Test]
        public void TheLegacyLevelApproved_ReadsAsCandidate()
        {
            var p = PlanParser.Parse("## F-X  X [art: approved] [slots: A.B]\n");
            Assert.That(p.Errors, Is.Empty);
            Assert.That(p.Feature("F-X").ArtLevel, Is.EqualTo("candidate"));
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
            Assert.That(p.Errors.Single(), Does.Contain("[art: shiny] must be one of graybox, candidate, final"));
            Assert.That(p.Feature("F-X").Slots, Is.EqualTo(new[] { "A.B", "A.C" }));
        }
    }
}
