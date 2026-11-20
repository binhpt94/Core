using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Game.Core.EditorTools.Tests
{
    /// <summary>
    /// What the art pictures rely on, checked WITHOUT rendering (runs anywhere: Editor with UI, batchmode, -nographics).
    /// Taking the pictures is a tool, not a test (it needs a GPU and writes the owner's dashboard files):
    /// Tools > Game > Art > Snapshot with the Editor open, `./Tools/test-gate.sh art shots` with it closed.
    /// </summary>
    [Category("ArtShots")]
    public sealed class ArtSnapshotTests
    {
        [Test]
        public void EveryArtPlacement_NamesARealSlotAndFixture_AndFindsTheSlot()
        {
            var slots = ArtProbe.Contracts().Select(c => c.Id).ToList();
            foreach (var cases in ViewCasesRegistry.All())
                foreach (var p in cases.ArtPlacements)
                {
                    string at = cases.ViewId + " places " + p.SlotId;
                    Assert.That(slots, Does.Contain(p.SlotId), at + ": no such art slot");
                    Assert.That(cases.FixtureNames, Does.Contain(p.Fixture), at + ": no such fixture");
                    foreach (var device in cases.Devices)
                        using (var stage = ViewStage.Open(cases, p.Fixture, device))
                            Assert.That(p.Find(stage.Root).Count(), Is.GreaterThan(0), at + " in fixture " + p.Fixture + "@" + device.Name + ": found nothing");
                }
        }

        [Test]
        public void ShotNames_AreWhatTheComparePageReads()
        {
            Assert.That(ArtSnapshot.SlotFile("A.Hero", Game.Core.Presentation.ArtLevel.Candidate), Is.EqualTo("A.Hero.candidate.png"));
            Assert.That(ArtSnapshot.InViewFile("A.Hero", "phone-16x9"), Is.EqualTo("A.Hero.inview.phone-16x9.png"));
        }
    }
}
