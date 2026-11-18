using System.Collections.Generic;
using System.Linq;
using Game.Core.DevTools;
using NUnit.Framework;

namespace Game.Core.EditorTools.Tests
{
    /// <summary>
    /// The UI layout gate, generated for EVERY view in the project (via its ViewCases), no per-view test code:
    ///   L2 Constraints(view, fixture, device): safe area, overlap, clipped text, touch targets.
    ///   L3 Snapshot(view): the text snapshot of all fixtures × devices equals the committed baseline.
    /// Tests carry the category "View:Feature.View", so the gate runs only the views a change can affect:
    ///   -testCategory "View:MathQuiz.QuizView"
    /// EditMode, no scene, no Play Mode, no game boot.
    /// </summary>
    public sealed class ViewLayoutTests
    {
        private static IEnumerable<TestCaseData> ConstraintCases()
        {
            foreach (var cases in ViewCasesRegistry.All())
                foreach (var fixture in cases.FixtureNames)
                    foreach (var device in cases.Devices)
                        yield return new TestCaseData(cases.ViewId, fixture, device.Name)
                            .SetCategory(ViewCasesRegistry.Category(cases.ViewId))
                            .SetName("Layout " + cases.ViewId + " [" + fixture + "@" + device.Name + "]");
        }

        private static IEnumerable<TestCaseData> SnapshotCases() =>
            ViewCasesRegistry.All().Select(c => new TestCaseData(c.ViewId)
                .SetCategory(ViewCasesRegistry.Category(c.ViewId))
                .SetName("Snapshot " + c.ViewId));

        [TestCaseSource(nameof(ConstraintCases))]
        public void Constraints(string viewId, string fixture, string deviceName)
        {
            var cases = ViewCasesRegistry.Get(viewId);
            var device = cases.Devices.Single(d => d.Name == deviceName);
            using (var stage = ViewStage.Open(cases, fixture, device))
            {
                var violations = stage.Check(cases.Limits);
                if (violations.Count > 0)
                    Assert.Fail(string.Join("\n", violations.Take(10).Select(v => viewId + "[" + fixture + "@" + deviceName + "] " + v)));
            }
        }

        [TestCaseSource(nameof(SnapshotCases))]
        public void Snapshot(string viewId)
        {
            var cases = ViewCasesRegistry.Get(viewId);
            var diff = LayoutBaseline.Compare(cases, LayoutBaseline.Render(cases));
            if (diff.Count > 0)
                Assert.Fail("Layout changed. If intended, an owner regenerates with " + LayoutBaseline.WriteVariable + "=1 and reviews:\n" + string.Join("\n", diff));
        }
    }
}
