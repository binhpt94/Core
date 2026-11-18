using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TestGate.Planning
{
    public enum VerdictKind { Passed, Failed, Unknown }

    /// <summary>One test case as a runner reported it.</summary>
    public sealed class CaseResult
    {
        public string Name;
        /// <summary>Raw outcome: Passed, Failed, Skipped, Inconclusive, Warning, NotExecuted, ... (anything else is unknown).</summary>
        public string Outcome;
        /// <summary>[Explicit] and not selected: excluded on purpose, counted neither as passed nor as unknown.</summary>
        public bool Excluded;
        public string Message;
    }

    /// <summary>What a run proves. The gate returns Kind == Passed, the dashboard records Status: one value, never two.</summary>
    public sealed class Verdict
    {
        public VerdictKind Kind;
        public int Total, Passed, Failed, Unknown, Excluded;
        public List<CaseResult> FailedCases = new List<CaseResult>();
        public List<CaseResult> UnknownCases = new List<CaseResult>();
        public string Reason;

        public bool IsPassed => Kind == VerdictKind.Passed;
        public string Status => Kind == VerdictKind.Passed ? "passed" : Kind == VerdictKind.Failed ? "failed" : "unknown";
    }

    /// <summary>
    /// The single pass/fail decision for every test runner (dotnet TRX, Unity NUnit XML). WHITELIST: only "Passed" passes.
    ///   passed  ⇔ exit 0, at least one Passed case, every counted case Passed, no run/suite-level failure
    ///   failed  ⇔ a Failed case, a run/suite-level failure, or a non-zero exit (even with clean-looking results)
    ///   unknown ⇔ otherwise: 0 counted cases, or a case that is Skipped/Ignored/Inconclusive/Warning/NotExecuted/empty/odd
    /// Unknown never counts as passed (same rule as TestResultsIndex: only "Passed" proves a requirement).
    /// </summary>
    public static class TestVerdict
    {
        public static Verdict Decide(int exitCode, IReadOnlyList<CaseResult> cases, IReadOnlyList<string> runFailures = null, int? reportedTotal = null)
        {
            var v = new Verdict();
            var counted = cases.Where(c => !c.Excluded).ToList();
            v.Excluded = cases.Count - counted.Count;
            v.Total = counted.Count;
            foreach (var c in counted)
            {
                if (c.Outcome == "Passed")
                    v.Passed++;
                else if (c.Outcome == "Failed")
                    v.FailedCases.Add(c);
                else
                    v.UnknownCases.Add(c);
            }
            v.Failed = v.FailedCases.Count;
            v.Unknown = v.UnknownCases.Count;
            var runFailed = runFailures ?? new List<string>();

            if (v.Failed > 0 || runFailed.Count > 0)
            {
                v.Kind = VerdictKind.Failed;
                v.Reason = string.Join("; ", runFailed.Concat(v.FailedCases.Take(3).Select(c => c.Name)));
            }
            else if (exitCode != 0)
            {
                v.Kind = VerdictKind.Failed;
                v.Reason = "runner exit " + exitCode + " although no case failed (partial results or a crash after writing them)";
            }
            else if (v.Total == 0)
            {
                v.Kind = VerdictKind.Unknown;
                v.Reason = "0 tests ran" + (v.Excluded > 0 ? " (" + v.Excluded + " explicit, not selected)" : "") + ": wrong assembly/category names, or tests failed to load";
            }
            else if (reportedTotal.HasValue && reportedTotal.Value != cases.Count)
            {
                v.Kind = VerdictKind.Unknown;
                v.Reason = "the run reports " + reportedTotal.Value + " tests but lists " + cases.Count + " (truncated results?)";
            }
            else if (v.Unknown > 0)
            {
                v.Kind = VerdictKind.Unknown;
                v.Reason = v.Unknown + " test(s) not proven (" + string.Join(", ", v.UnknownCases.Select(c => string.IsNullOrEmpty(c.Outcome) ? "no result" : c.Outcome).Distinct()) +
                           "): " + string.Join("; ", v.UnknownCases.Take(3).Select(c => c.Name));
            }
            else
                v.Kind = VerdictKind.Passed;
            return v;
        }

        /// <summary>NUnit 3 XML (Unity Test Framework). Suite failures outside their children (SetUp/TearDown, load errors) count.</summary>
        public static Verdict FromNUnit(int exitCode, XDocument doc)
        {
            var cases = doc.Descendants("test-case").Select(tc => new CaseResult
            {
                Name = (string)tc.Attribute("fullname") ?? (string)tc.Attribute("name"),
                Outcome = (string)tc.Attribute("result"),
                Excluded = (string)tc.Attribute("result") == "Skipped" && (string)tc.Attribute("label") == "Explicit",
                Message = (string)tc.Descendants("message").FirstOrDefault()
            }).ToList();
            var runFailures = doc.Descendants("test-suite")
                .Where(s => (string)s.Attribute("result") == "Failed" && (string)s.Attribute("site") != "Child")
                .Select(s => "suite " + ((string)s.Attribute("fullname") ?? (string)s.Attribute("name")) + " failed (" + ((string)s.Attribute("site") ?? (string)s.Attribute("label") ?? "?") + ")")
                .ToList();
            var run = doc.Root;
            if (run != null && run.Name.LocalName == "test-run" && (string)run.Attribute("result") == "Failed" && runFailures.Count == 0 && !cases.Any(c => c.Outcome == "Failed"))
                runFailures.Add("test-run failed");
            int? total = run != null && int.TryParse((string)run.Attribute("total"), out var t) ? t : (int?)null;
            return Decide(exitCode, cases, runFailures, total);
        }

        /// <summary>Visual Studio TRX (dotnet test). Error/Timeout/Aborted are failures; NotExecuted/Inconclusive/Warning are unknown.</summary>
        public static Verdict FromTrx(int exitCode, XDocument doc)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var cases = doc.Descendants(ns + "UnitTestResult").Select(r =>
            {
                string o = (string)r.Attribute("outcome");
                return new CaseResult
                {
                    Name = (string)r.Attribute("testName"),
                    Outcome = o == "Error" || o == "Timeout" || o == "Aborted" ? "Failed" : o,
                    Message = (string)r.Descendants(ns + "Message").FirstOrDefault()
                };
            }).ToList();
            var summary = doc.Descendants(ns + "ResultSummary").FirstOrDefault();
            string outcome = (string)summary?.Attribute("outcome");
            var runFailures = new List<string>();
            if (outcome == "Failed" || outcome == "Error" || outcome == "Aborted" || outcome == "Timeout")
                if (!cases.Any(c => c.Outcome == "Failed"))
                    runFailures.Add("test run " + outcome);
            var counters = summary?.Element(ns + "Counters");
            int? total = int.TryParse((string)counters?.Attribute("total"), out var t) ? t : (int?)null;
            return Decide(exitCode, cases, runFailures, total);
        }
    }
}
