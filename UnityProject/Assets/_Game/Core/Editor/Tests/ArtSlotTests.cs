using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Game.Core.EditorTools.Tests
{
    /// <summary>
    /// The ART GATE, generated for every slot × level (graybox always; candidate/final when bound). EditMode, no scene.
    /// Category "Art:Feature.Name" lets the test gate run only the slot whose art changed.
    /// </summary>
    public sealed class ArtSlotTests
    {
        private static IEnumerable<TestCaseData> Cases() =>
            ArtProbe.Cases().Select(c => new TestCaseData(c.Contract.Id, c.Level.ToString())
                .SetCategory(ArtProbe.Category(c.Contract.Id))
                .SetName("Art " + c.Name));

        [TestCaseSource(nameof(Cases))]
        public void MeetsItsContract(string slotId, string level)
        {
            var art = ArtProbe.Cases().Single(c => c.Contract.Id == slotId && c.Level.ToString() == level);
            var m = ArtProbe.Measure(art);
            var violations = ArtChecks.Check(art.Contract, m);
            ArtProbe.Record(art, m, violations);
            if (violations.Count > 0)
                Assert.Fail(art.Name + "\n" + string.Join("\n", violations) +
                            "\nFix the art (size, pivot, clip timing) or, if the design must change, ask the human to change the contract.");
        }
    }
}
