using System.Linq;
using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;
using Game.TestSupport;
using NUnit.Framework;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// The story's experience goals, checked over CONTENT (the shipped archetype and config). A content swap that keeps
    /// them passes without anyone's approval; one that breaks them turns the gate red, and only then a human decides.
    /// </summary>
    [Category("Design")]
    public sealed class DesignLintTests
    {
        /// <summary>Code defaults AND every QuizConfigAsset in the project (what designers changed in the Inspector).</summary>
        private static System.Collections.Generic.IEnumerable<(string Source, ArenaConfig Config)> Shipped()
        {
            yield return ("ArenaConfig.Default", ArenaConfig.Default);
            foreach (var (path, designer) in ShippedContent.ArenaAssets())
            {
                var c = designer.ToArenaConfig();
                Assert.That(c.IsSuccess, Is.True, path + ": " + c.Error);
                yield return (path, c.Value);
            }
        }

        [Test, Covers("R-ARENA-BREATHER")]
        public void ShippedArena_GivesTheBreather()
        {
            foreach (var (source, c) in Shipped())
                Assert.That(ArenaDesign.FirstContactTicks(c), Is.GreaterThanOrEqualTo(ArenaDesign.BreatherSeconds * ArenaConfig.TickRate),
                    source + " breaks R-ARENA-BREATHER. If a designer changed it on purpose, the human decides: change the numbers or the goal in plan/features.md");
        }

        [Test, Covers("R-ARENA-READABLE")]
        public void ShippedArena_RagingStaysReadable()
        {
            foreach (var (source, c) in Shipped())
                Assert.That(ArenaDesign.RagingSpeed(c), Is.LessThanOrEqualTo(ArenaDesign.MaxReadableSpeed), source + " breaks R-ARENA-READABLE");
        }

        [Test]
        public void DesignerDefaults_EqualTheCodeDefaults() =>
            Assert.That(DesignerArena.Default.ToArenaConfig().Value.Canonical(), Is.EqualTo(ArenaConfig.Default.Canonical()));

        [Test]
        public void AnInspectorEdit_IsCheckedLikeCode()
        {
            // A designer drags the slime speed up to 400 lane units/s in the Inspector (Force Text YAML):
            var asset = "MonoBehaviour:\n  baseLives: 3\n  spawnEverySeconds: {x: 2, y: 4}\n  slimeSpeedPerSecond: {x: 300, y: 400}\n  hopPercent: 25\n";
            var c = ShippedContent.ParseArena(asset).ToArenaConfig().Value;
            Assert.That(ArenaDesign.Violations(c), Has.Some.StartsWith("breather"));
        }

        [Test]
        public void TheShippedConfig_IsTheActiveArchetype_SoSwappingContentIsOneLine()
        {
            var fromContent = ArenaConfig.Default.WithArchetype(EnemyArchetypes.Active).Value;
            Assert.That(fromContent.Canonical(), Is.EqualTo(ArenaConfig.Default.Canonical()), "replays and goldens are unaffected");
        }

        [Test]
        public void ExampleSwap_Bat_BreaksTheBreather_SoItNeedsADecision_NotAMeeting()
        {
            // The "Sói 3 skill → Hổ 2 skill" situation: the bat candidate is faster. The lint says exactly which goal breaks and
            // by how much; the human either keeps the slime, tunes the bat, or changes R-ARENA-BREATHER in the plan.
            var bat = ArenaConfig.Default.WithArchetype(EnemyArchetypes.Bat).Value;
            var broken = ArenaDesign.Violations(bat);
            Assert.That(broken, Has.Count.EqualTo(1));
            StringAssert.StartsWith("breather: first contact after 83 ticks < 100", broken[0]);
        }

        [Test]
        public void EveryArchetype_HasAnArtSlotContract_AndEveryContractIsSane()
        {
            var slots = new MathQuizArtSlots().Slots;
            foreach (var a in EnemyArchetypes.All)
                Assert.That(slots.Any(s => s.Id == a.ArtSlot), Is.True, a.Id + " has no art slot");
            foreach (var s in slots)
            {
                Assert.That(s.Problems(), Is.Empty, s.Id);
                Assert.That(ArtChecks.Check(s, ArtChecks.Graybox(s)), Is.Empty, s.Id + ": the graybox must meet its own contract");
            }
        }
    }
}
