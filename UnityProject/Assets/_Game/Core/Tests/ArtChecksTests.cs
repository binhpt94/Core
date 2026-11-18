using System.Linq;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class ArtChecksTests
    {
        private static ArtSlotContract Boss(bool hitRequired = true) => new ArtSlotContract("Forest.Boss", ArtSlotKind.Model3D,
            minWidth: 160, maxWidth: 260, minHeight: 180, maxHeight: 300, pivotXPercent: 50, pivotYPercent: 0, pivotTolerancePercent: 5, tickRate: 20,
            actions: new[] { new ArtAction("Swipe", "hit", minTick: 8, maxTick: 10, minSpeedPercent: 90, maxSpeedPercent: 110, required: hitRequired) });

        private static ArtMeasurement Tiger(int width = 220, int hitMs = 450, bool clip = true)
        {
            var m = new ArtMeasurement { Width = width, Height = 240, PivotXPercent = 50, PivotYPercent = 2 };
            if (clip)
            {
                var c = new ArtClip { LengthMs = 900 };
                c.MarkersMs["hit"] = hitMs;
                m.Clips["Swipe"] = c;
            }
            return m;
        }

        [Test]
        public void ArtWithinTheContract_Passes() => Assert.That(ArtChecks.Check(Boss(), Tiger()), Is.Empty);

        [Test]
        public void TooBig_SaysByHowMuch()
        {
            Assert.That(ArtChecks.Check(Boss(), Tiger(width: 320)), Is.EqualTo(new[] { "A-SIZE width 320 > max 260" }));
        }

        [Test]
        public void HitMarker_OutsideTheGameplayWindow_EvenAtTheAllowedSpeeds_Fails()
        {
            // Window = ticks 8..10 = 400..500 ms. Marker at 600 ms plays at 545..666 ms (110%..90%): never inside.
            var v = ArtChecks.Check(Boss(), Tiger(hitMs: 600));
            Assert.That(v.Single(), Does.StartWith("A-TIME 'Swipe.hit' at 600 ms can play at 545-666 ms, gameplay window is 400-500 ms"));
            // 540 ms at 110% → 490 ms: inside. Playback speed absorbs small differences; gameplay timing never moves.
            Assert.That(ArtChecks.Check(Boss(), Tiger(hitMs: 540)), Is.Empty);
        }

        [Test]
        public void MissingClip_IsAnErrorOnlyWhenRequired_MissingMarkerAlways()
        {
            Assert.That(ArtChecks.Check(Boss(), Tiger(clip: false)), Is.EqualTo(new[] { "A-CLIP missing clip 'Swipe'" }));
            Assert.That(ArtChecks.Check(Boss(hitRequired: false), Tiger(clip: false)), Is.Empty, "a static asset is fine for an optional action");
            var noMarker = Tiger();
            noMarker.Clips["Swipe"].MarkersMs.Clear();
            Assert.That(ArtChecks.Check(Boss(hitRequired: false), noMarker), Is.EqualTo(new[] { "A-MARK clip 'Swipe' has no marker 'hit'" }));
        }

        [Test]
        public void Pivot_IsChecked_WithTolerance()
        {
            var m = Tiger();
            m.PivotYPercent = 30;
            Assert.That(ArtChecks.Check(Boss(), m).Single(), Does.StartWith("A-PIVOT"));
        }

        [Test]
        public void TheGraybox_AlwaysMeetsItsContract_AndContractsAreValidated()
        {
            Assert.That(ArtChecks.Check(Boss(), ArtChecks.Graybox(Boss())), Is.Empty);
            Assert.That(ArtChecks.Describe("Forest.Boss", "graybox", ArtChecks.Graybox(Boss())), Is.EqualTo("Forest.Boss [graybox] 210x240 pivot 50%,0% Swipe:600ms/hit@450"));
            var bad = new ArtSlotContract("NoDot", ArtSlotKind.UiImage, 10, 5, 1, 1, 50, 50, 0, 20, new[] { new ArtAction("A", "m", 5, 2) });
            Assert.That(bad.Problems(), Has.Count.EqualTo(3));
        }
    }
}
