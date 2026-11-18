using System.Linq;
using Game.Core.DevTools;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class LayoutChecksTests
    {
        private static readonly LayoutRect Screen = new LayoutRect(0, 0, 1080, 1920);

        private static LayoutNode Tree(params LayoutNode[] layers)
        {
            var root = new LayoutNode("Canvas", LayoutNodeKind.Container, Screen);
            foreach (var l in layers)
                root.Add(l);
            return root;
        }

        private static LayoutNode Node(string name, LayoutNodeKind kind, int x, int y, int w, int h, int need = -1) =>
            new LayoutNode(name, kind, new LayoutRect(x, y, w, h), need);

        [Test]
        public void ContentOutsideTheSafeArea_IsReported_WithTheDistance()
        {
            var root = Tree(Node("Main", LayoutNodeKind.Decoration, 0, 0, 1080, 1920)
                .Add(Node("Title", LayoutNodeKind.Text, 60, 40, 960, 100)));
            var safe = new LayoutRect(0, 118, 1080, 1700);

            var v = LayoutChecks.Check(root, safe);

            Assert.That(v.Select(x => x.ToString()), Is.EqualTo(new[] { "L-SAFE Main/Title outside safe area by 78 top (rect 60,40 960x100, safe 0,118 1080x1700)" }));
        }

        [Test]
        public void Decorations_MayBleedOutsideTheSafeArea()
        {
            var root = Tree(Node("Background", LayoutNodeKind.Decoration, 0, 0, 1080, 1920));
            Assert.That(LayoutChecks.Check(root, new LayoutRect(0, 100, 1080, 1700)), Is.Empty);
        }

        [Test]
        public void OverlappingButtons_InOneLayer_AreReported_ButAnOverlayLayerMayCoverThem()
        {
            var main = Node("Main", LayoutNodeKind.Container, 0, 0, 1080, 1920)
                .Add(Node("A", LayoutNodeKind.Interactive, 100, 100, 300, 200))
                .Add(Node("B", LayoutNodeKind.Interactive, 390, 100, 300, 200));
            var overlay = Node("Popup", LayoutNodeKind.Decoration, 0, 0, 1080, 1920)
                .Add(Node("Close", LayoutNodeKind.Interactive, 100, 100, 300, 200));

            var v = LayoutChecks.Check(Tree(main, overlay), Screen);

            Assert.That(v.Select(x => x.ToString()), Is.EqualTo(new[] { "L-OVERLAP Main/A overlaps Main/B by 10x200" }));
        }

        [Test]
        public void ALabelInsideItsButton_IsNotAnOverlap()
        {
            var button = Node("Ok", LayoutNodeKind.Interactive, 100, 100, 300, 200)
                .Add(Node("Label", LayoutNodeKind.Text, 100, 100, 300, 200, need: 120));
            Assert.That(LayoutChecks.Check(Tree(button), Screen), Is.Empty);
        }

        [Test]
        public void ClippedText_TooSmallTouchTargets_AreReported()
        {
            var root = Tree(Node("Main", LayoutNodeKind.Container, 0, 0, 1080, 1920)
                .Add(Node("Question", LayoutNodeKind.Text, 60, 500, 960, 200, need: 370))
                .Add(Node("Tiny", LayoutNodeKind.Interactive, 60, 900, 80, 80)));

            var codes = LayoutChecks.Check(root, Screen).Select(x => x.Code);

            Assert.That(codes, Is.EquivalentTo(new[] { "TEXT", "TOUCH" }));
        }

        [Test]
        public void OnePixelOfRounding_IsTolerated_TwoAreNot()
        {
            LayoutNode Root(int y) => Tree(Node("Main", LayoutNodeKind.Container, 0, 0, 1080, 1920)
                .Add(Node("A", LayoutNodeKind.Interactive, 0, y, 200, 200)));
            Assert.That(LayoutChecks.Check(Root(-1), Screen), Is.Empty);
            Assert.That(LayoutChecks.Check(Root(-2), Screen), Has.Count.EqualTo(1));
        }

        [Test]
        public void EmptyTextAndHiddenSizes_AreIgnored()
        {
            var root = Tree(Node("Main", LayoutNodeKind.Container, 0, 0, 1080, 1920)
                .Add(new LayoutNode("Feedback", LayoutNodeKind.Text, new LayoutRect(-50, 0, 100, 10), 999, isEmptyText: true))
                .Add(Node("Collapsed", LayoutNodeKind.Interactive, -500, 0, 0, 0)));
            Assert.That(LayoutChecks.Check(root, Screen), Is.Empty);
        }
    }

    public sealed class LayoutSnapshotTests
    {
        private static LayoutCase Case(int questionY, int questionH)
        {
            var root = new LayoutNode("Canvas", LayoutNodeKind.Container, new LayoutRect(0, 0, 1080, 1920))
                .Add(new LayoutNode("Background", LayoutNodeKind.Decoration, new LayoutRect(0, 0, 1080, 1920))
                    .Add(new LayoutNode("Question", LayoutNodeKind.Text, new LayoutRect(60, questionY, 960, questionH)))
                    .Add(new LayoutNode("Next", LayoutNodeKind.Interactive, new LayoutRect(60, 1500, 960, 180))));
            var canvas = new LayoutRect(0, 0, 1080, 1920);
            return new LayoutCase("smoke", "phone-16x9", canvas, canvas, root);
        }

        [Test]
        public void Snapshot_IsOneLinePerNode_WithKindAndRect()
        {
            string text = LayoutSnapshot.Render("MathQuiz/QuizView", new[] { Case(520, 400) });

            StringAssert.StartsWith(LayoutSnapshot.Header + ": MathQuiz/QuizView\n", text);
            StringAssert.Contains("== smoke@phone-16x9 canvas=1080x1920 safe=0,0 1080x1920\n", text);
            StringAssert.Contains("Background D 0,0 1080x1920\n", text);
            StringAssert.Contains("Background/Question T 60,520 960x400\n", text);
            StringAssert.Contains("Background/Next I 60,1500 960x180\n", text);
        }

        [Test]
        public void TwoPixelMove_IsExactlyOneReadableDiffLine()
        {
            string before = LayoutSnapshot.Render("V", new[] { Case(520, 400) });
            string after = LayoutSnapshot.Render("V", new[] { Case(519, 402) });

            Assert.That(LayoutSnapshot.Diff(before, after), Is.EqualTo(new[] { "smoke@phone-16x9 Background/Question: T 60,520 960x400 -> T 60,519 960x402" }));
            Assert.That(LayoutSnapshot.Diff(before, before), Is.Empty);
        }

        [Test]
        public void AddedAndRemovedNodes_AndNewCases_AreReported()
        {
            string before = LayoutSnapshot.Render("V", new[] { Case(520, 400) });
            string after = before.Replace("Background/Next I 60,1500 960x180\n", "Background/Skip I 60,1500 960x180\n");

            var diff = LayoutSnapshot.Diff(before, after);

            Assert.That(diff, Has.Some.EqualTo("smoke@phone-16x9 Background/Next: removed (was I 60,1500 960x180)"));
            Assert.That(diff, Has.Some.EqualTo("smoke@phone-16x9 Background/Skip: added I 60,1500 960x180"));
            Assert.That(LayoutSnapshot.Diff("", before), Has.Some.StartsWith("smoke@phone-16x9 (canvas): added"));
        }
    }

    public sealed class DeviceProfileTests
    {
        [Test]
        public void ReferenceResolution_MapsOneToOne()
        {
            var d = new DeviceProfile("ref", 1080, 1920);
            Assert.That(d.Canvas(1080, 1920, 0.5).ToString(), Is.EqualTo("0,0 1080x1920"));
        }

        [Test]
        public void Notch_SafeArea_IsConvertedToCanvasUnits_LikeCanvasScaler()
        {
            var notch = DeviceProfile.Standard().Single(d => d.Name == "phone-notch");
            // scale = sqrt(1170/1080 * 2532/1920) = 1.1952
            Assert.That(notch.Canvas(1080, 1920, 0.5).ToString(), Is.EqualTo("0,0 979x2118"));
            Assert.That(notch.SafeArea(1080, 1920, 0.5).ToString(), Is.EqualTo("0,118 979x1915"));
        }

        [Test]
        public void Tablet_GetsAShorterCanvas()
        {
            var tablet = DeviceProfile.Standard().Single(d => d.Name == "tablet-4x3");
            Assert.That(tablet.Canvas(1080, 1920, 0.5).H, Is.LessThan(1700), "tablets are where vertical layouts overflow");
        }

        [Test]
        public void FixtureNames_AreIds()
        {
            Assert.Throws<System.ArgumentException>(() => new ViewFixture<object>("has space", _ => { }));
            Assert.That(new ViewFixture<object>("smoke", _ => { }, FixtureTags.Smoke).Has(FixtureTags.Smoke), Is.True);
        }
    }
}
