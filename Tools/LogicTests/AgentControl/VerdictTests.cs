using System.Xml.Linq;
using NUnit.Framework;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// The one pass/fail decision of every runner (TestVerdict). Whitelist: only "Passed" proves anything; the gate's return
    /// value and the dashboard record both come from the same Verdict. No Unity needed to catch a regression here.
    /// </summary>
    public sealed class VerdictTests
    {
        private static XDocument NUnit(string cases, string runResult = "Passed", string total = null, string suites = "") =>
            XDocument.Parse("<test-run result=\"" + runResult + "\"" + (total != null ? " total=\"" + total + "\"" : "") + ">" +
                            "<test-suite type=\"Assembly\" name=\"Game.A.Tests\" result=\"" + runResult + "\" site=\"Child\">" + suites + cases +
                            "</test-suite></test-run>");

        private static string Case(string name, string result, string label = null) =>
            "<test-case name=\"" + name + "\" fullname=\"Game.A.Tests." + name + "\" result=\"" + result + "\"" +
            (label != null ? " label=\"" + label + "\"" : "") + " />";

        private const string TrxNs = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        private static XDocument Trx(string summary, params (string name, string outcome)[] results)
        {
            string rs = "";
            foreach (var (n, o) in results)
                rs += "<UnitTestResult testName=\"" + n + "\" outcome=\"" + o + "\" />";
            return XDocument.Parse("<TestRun xmlns=\"" + TrxNs + "\"><Results>" + rs + "</Results>" +
                                   "<ResultSummary outcome=\"" + summary + "\"><Counters total=\"" + results.Length + "\" /></ResultSummary></TestRun>");
        }

        [Test]
        public void AllPassed_ExitZero_IsTheOnlyPass()
        {
            var v = TestVerdict.FromNUnit(0, NUnit(Case("A", "Passed") + Case("B", "Passed"), total: "2"));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Passed), v.Reason);
            Assert.That(v.Status, Is.EqualTo("passed"));
            Assert.That(v.Passed, Is.EqualTo(2));
        }

        [Test]
        public void ZeroTests_IsUnknown_EvenWithoutCategories()
        {
            var v = TestVerdict.FromNUnit(0, NUnit(""));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Unknown));
            Assert.That(v.IsPassed, Is.False);
            Assert.That(v.Reason, Does.Contain("0 tests"));
        }

        [TestCase("Skipped")]
        [TestCase("Inconclusive")]
        [TestCase("Warning")]
        [TestCase("")]
        [TestCase("Weird")]
        public void AnythingButPassed_IsUnknown_NeverPassed(string result)
        {
            var v = TestVerdict.FromNUnit(0, NUnit(Case("A", "Passed") + Case("B", result)));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Unknown), result);
            Assert.That(v.Passed, Is.EqualTo(1));
            Assert.That(v.Unknown, Is.EqualTo(1));
        }

        [Test]
        public void AllSkipped_IsUnknown_ButExplicitNotSelected_IsExcluded()
        {
            Assert.That(TestVerdict.FromNUnit(0, NUnit(Case("A", "Skipped", "Ignored") + Case("B", "Skipped"))).Kind, Is.EqualTo(VerdictKind.Unknown));
            var v = TestVerdict.FromNUnit(0, NUnit(Case("A", "Passed") + Case("Bench", "Skipped", "Explicit")));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Passed), v.Reason);
            Assert.That(v.Excluded, Is.EqualTo(1));
            Assert.That(TestVerdict.FromNUnit(0, NUnit(Case("Bench", "Skipped", "Explicit"))).Kind, Is.EqualTo(VerdictKind.Unknown), "only explicit = 0 tests ran");
        }

        [TestCase(2)]
        [TestCase(3)]
        public void NonZeroExit_WithCleanXml_IsFailed(int exit)
        {
            var v = TestVerdict.FromNUnit(exit, NUnit(Case("A", "Passed")));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Failed));
            Assert.That(v.Reason, Does.Contain("exit " + exit));
        }

        [Test]
        public void SuiteLevelFailure_SetUpOrLoad_IsFailed()
        {
            string brokenSetUp = "<test-suite type=\"SetUpFixture\" fullname=\"Game.A.Tests.Setup\" result=\"Failed\" site=\"SetUp\" />";
            var v = TestVerdict.FromNUnit(0, NUnit(Case("A", "Passed"), suites: brokenSetUp));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Failed));
            Assert.That(v.Reason, Does.Contain("SetUp"));
        }

        [Test]
        public void RunTotal_ThatDisagreesWithTheCases_IsUnknown()
        {
            Assert.That(TestVerdict.FromNUnit(0, NUnit(Case("A", "Passed"), total: "5")).Kind, Is.EqualTo(VerdictKind.Unknown));
        }

        [Test]
        public void FailedCase_IsFailed_AndNamed()
        {
            var v = TestVerdict.FromNUnit(2, NUnit(Case("A", "Failed") + Case("B", "Passed"), "Failed"));
            Assert.That(v.Kind, Is.EqualTo(VerdictKind.Failed));
            Assert.That(v.FailedCases[0].Name, Is.EqualTo("Game.A.Tests.A"));
        }

        [TestCase("NotExecuted", VerdictKind.Unknown)]
        [TestCase("Inconclusive", VerdictKind.Unknown)]
        [TestCase("Warning", VerdictKind.Unknown)]
        [TestCase("Timeout", VerdictKind.Failed)]
        [TestCase("Aborted", VerdictKind.Failed)]
        [TestCase("Error", VerdictKind.Failed)]
        public void Dotnet_UsesTheSameWhitelist(string outcome, VerdictKind expected)
        {
            var v = TestVerdict.FromTrx(0, Trx("Completed", ("A", "Passed"), ("B", outcome)));
            Assert.That(v.Kind, Is.EqualTo(expected), outcome);
        }

        [Test]
        public void Dotnet_ZeroTests_OrNonZeroExit_IsNotAPass()
        {
            Assert.That(TestVerdict.FromTrx(0, Trx("Completed")).Kind, Is.EqualTo(VerdictKind.Unknown));
            Assert.That(TestVerdict.FromTrx(1, Trx("Completed", ("A", "Passed"))).Kind, Is.EqualTo(VerdictKind.Failed));
            Assert.That(TestVerdict.FromTrx(0, Trx("Completed", ("A", "Passed"))).Kind, Is.EqualTo(VerdictKind.Passed));
        }
    }
}
