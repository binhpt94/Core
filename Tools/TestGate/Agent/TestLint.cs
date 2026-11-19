using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Static check of one test method before it can be sealed (deterministic, no AI review). A test must assert something real:
    ///   at least one assertion (directly, or through a helper method of the same file that asserts)
    ///   never Assert.Pass / Inconclusive / Ignore; never an assertion on a literal (Assert.That(true…)) or of a value against itself
    ///   never a catch that swallows the failure (no throw and no assert inside)
    /// Honest limit: it can't judge whether the expected VALUE is right; "red first" and the arbiter cover that.
    /// </summary>
    public static class TestLint
    {
        private static readonly Regex Assertion = new Regex(@"\b(Assert|StringAssert|CollectionAssert|FileAssert|DirectoryAssert)\.(?!Pass\b|Inconclusive\b|Ignore\b|Warn\b)\w+(<[^>]*>)?\s*\(");
        private static readonly Regex Escape = new Regex(@"\bAssert\.(Pass|Inconclusive|Ignore)\s*\(");
        private static readonly Regex OnLiteral = new Regex(@"\bAssert\.(That|True|IsTrue|False|IsFalse)\s*\(\s*(true|false)\s*[,)]");
        private static readonly Regex SelfCompare = new Regex(@"\bAssert\.That\(\s*([\w\.\[\]]+)\s*,\s*Is\.EqualTo\(\s*\1\s*\)");
        private static readonly Regex Catch = new Regex(@"\bcatch\b[^{]*\{");
        private static readonly Regex Call = new Regex(@"\b([A-Z]\w*)\s*\(");

        /// <param name="method">the test method's text (signature + body)</param>
        /// <param name="file">the whole test file (comments stripped): helper methods that assert count</param>
        public static List<string> Check(string method, string file)
        {
            var problems = new List<string>();
            if (method == null)
                return new List<string> { "test method not found" };
            int open = method.IndexOf('{');
            string body = open >= 0 ? method.Substring(open) : method.Substring(method.IndexOf("=>") + 2);
            if (body.Trim().Trim('{', '}', ';').Trim().Length == 0)
                problems.Add("empty test");
            foreach (Match m in Escape.Matches(body))
                problems.Add("Assert." + m.Groups[1].Value + " makes the test pass without checking anything");
            foreach (Match m in OnLiteral.Matches(body))
                problems.Add("assertion on a literal (" + m.Value.Trim() + "…) checks nothing");
            foreach (Match m in SelfCompare.Matches(body))
                problems.Add("compares " + m.Groups[1].Value + " with itself");
            foreach (Match m in Catch.Matches(body))
            {
                string block = Block(body, m.Index + m.Length - 1);
                if (!block.Contains("throw") && !Assertion.IsMatch(block))
                    problems.Add("a catch swallows the failure (no throw, no assert inside)");
            }
            if (!Assertion.IsMatch(body) && !HelperAsserts(body, file))
                problems.Add("no assertion (Assert.That / Assert.Throws / …): a test that can't fail proves nothing");
            return problems;
        }

        private static bool HelperAsserts(string body, string file)
        {
            if (file == null)
                return false;
            foreach (Match c in Call.Matches(body))
            {
                string name = c.Groups[1].Value;
                var decl = Regex.Match(file, @"\b" + name + @"\s*\([^)]*\)\s*(\{|=>)");
                if (!decl.Success)
                    continue;
                string helper = decl.Groups[1].Value == "{" ? Block(file, decl.Index + decl.Length - 1) : file.Substring(decl.Index, System.Math.Min(400, file.Length - decl.Index));
                if (Assertion.IsMatch(helper))
                    return true;
            }
            return false;
        }

        private static string Block(string text, int brace)
        {
            int depth = 0;
            for (int i = brace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(brace, i + 1 - brace);
            }
            return text.Substring(brace);
        }
    }
}
