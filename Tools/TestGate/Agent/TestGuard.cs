using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <param name="retired">requirements the task retires: tests covering only them may go (task start --retire)</param>
        public static List<string> Check(string path, string before, string after, ICollection<string> retired = null)
        {
            var findings = new List<string>();
            if (!IsTestFile(path) || before == null)
                return findings; // new test files only add coverage
            string b = SourceText.StripComments(before);
            if (retired != null && retired.Count > 0)
                b = WithoutRetired(b, retired);
            if (after == null)
            {
                int left = Count(TestAttr, b);
                if (left > 0)
                    findings.Add(path + ": test file deleted (" + left + " tests" + (retired != null && retired.Count > 0 ? " not covering only retired requirements" : "") + ")");
                return findings;
            }

            string a = SourceText.StripComments(after);
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

        private static readonly Regex CoversAttr = new Regex(@"\bCovers\s*\(\s*""(?<id>[^""]*)""\s*\)");
        private static readonly Regex NextDecl = new Regex(@"\bclass\s+\w+|\b(?:void|Task|UniTask|IEnumerator)\s+\w+\s*\(");

        /// <summary>
        /// Comment-stripped test code minus the methods/classes whose [Covers] all name retired requirements: what the
        /// task must keep. Members are cut from the end of the previous member to their closing brace (or `=> …;`).
        /// </summary>
        public static string WithoutRetired(string code, ICollection<string> retired)
        {
            var groups = CoversAttr.Matches(code).Cast<Match>()
                .Select(m => (m, decl: NextDecl.Match(code, m.Index + m.Length)))
                .Where(x => x.decl.Success)
                .GroupBy(x => x.decl.Index)
                .Where(g => g.All(x => retired.Contains(x.m.Groups["id"].Value)))
                .Select(g => (start: code.LastIndexOfAny(new[] { '}', ';', '{' }, g.Min(x => x.m.Index)) + 1, decl: g.Key))
                .OrderByDescending(x => x.decl)
                .ToList();
            foreach (var (start, decl) in groups)
            {
                int end = EndOfMember(code, decl);
                if (end > start)
                    code = code.Remove(start, end - start);
            }
            return code;
        }

        private static int EndOfMember(string code, int decl)
        {
            int brace = code.IndexOf('{', decl), arrow = code.IndexOf("=>", decl, StringComparison.Ordinal);
            if (arrow >= 0 && (brace < 0 || arrow < brace))
            {
                int semi = code.IndexOf(';', arrow);
                return semi < 0 ? code.Length : semi + 1;
            }
            if (brace < 0)
                return -1;
            int depth = 0;
            for (int i = brace; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return i + 1;
            }
            return code.Length;
        }
    }
}
