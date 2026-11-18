using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>
    /// "Fake fix" detector on the DIFF of test files (a per-file rule can't see what was there before):
    /// fewer assertions, fewer tests, new Ignore/Assume/Inconclusive/Explicit/Pass, new #if around tests, deleted test files.
    /// Allowed only when a human approved test changes for the task (`task request --tests`). Deterministic, no AI review.
    /// Honest limit: a weaker assertion with the same count (Is.EqualTo → Is.Not.Null) is caught by golden data, not here.
    /// </summary>
    public static class TestGuard
    {
        private static readonly Regex Assertion = new Regex(@"\b(Assert|StringAssert|CollectionAssert|FileAssert|LogAssert)\.\w+\s*\(");
        private static readonly Regex TestAttr = new Regex(@"\[\s*(Test|TestCase|TestCaseSource|UnityTest|Theory)\b");
        private static readonly (string Name, Regex Regex)[] Escapes =
        {
            ("[Ignore]", new Regex(@"[\[,]\s*Ignore\b")),
            ("[Explicit]", new Regex(@"[\[,]\s*Explicit\b")),
            ("Assert.Ignore", new Regex(@"\bAssert\.Ignore\s*\(")),
            ("Assert.Pass", new Regex(@"\bAssert\.Pass\s*\(")),
            ("Assert.Inconclusive", new Regex(@"\bAssert\.Inconclusive\s*\(")),
            ("Assume.That", new Regex(@"\bAssume\.That\s*\(")),
            ("#if", new Regex(@"^\s*#\s*if\b", RegexOptions.Multiline)),
        };

        public static bool IsTestFile(string path) => path.EndsWith(".cs", StringComparison.Ordinal) && PathInfo.Of(path).IsTest;

        /// <param name="before">content at the base commit (null = new file)</param>
        /// <param name="after">current content (null = deleted)</param>
        public static List<string> Check(string path, string before, string after)
        {
            var findings = new List<string>();
            if (!IsTestFile(path) || before == null)
                return findings; // new test files only add coverage
            if (after == null)
            {
                findings.Add(path + ": test file deleted (" + Count(TestAttr, before) + " tests)");
                return findings;
            }

            string b = SourceText.StripComments(before), a = SourceText.StripComments(after);
            int ab = Count(Assertion, b), aa = Count(Assertion, a);
            if (aa < ab)
                findings.Add(path + ": assertions " + ab + " -> " + aa);
            int tb = Count(TestAttr, b), ta = Count(TestAttr, a);
            if (ta < tb)
                findings.Add(path + ": tests " + tb + " -> " + ta);
            foreach (var (name, regex) in Escapes)
            {
                int eb = Count(regex, b), ea = Count(regex, a);
                if (ea > eb)
                    findings.Add(path + ": added " + name + " (" + eb + " -> " + ea + ")");
            }
            return findings;
        }

        private static int Count(Regex r, string text) => r.Matches(text).Count;
    }
}
