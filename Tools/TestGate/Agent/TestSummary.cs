using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>
    /// What the tests of a requirement check, in one line per test: the method name as words and its assertions shortened
    /// ("Three correct → gives 15 coins: coins == 15"). Shown at `task close` (the tests that task added or changed) and by
    /// `tests summary [R-ID|F-ID]`. Read-only: it blocks nothing; it is what the owner skims instead of reading test code.
    /// </summary>
    public static class TestSummary
    {
        private const int MaxAssertLength = 100;
        private static readonly Regex AssertStart = new Regex(@"\b(Assert|StringAssert|CollectionAssert)\.(\w+)(<[^>()]*>)?\s*\(");
        private static readonly Regex Hump = new Regex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])");

        public sealed class Entry
        {
            public string Requirement { get; set; }
            public string TestKey { get; set; }
            public string Title { get; set; }
            public List<string> Asserts { get; set; } = new List<string>();
            public string Change { get; set; }   // "new" | "changed" | null (unchanged or no base)
        }

        // ------------------------------------------------------------------ text (pure)

        /// <summary>ThreeCorrect_Gives15Coins → "Three correct → gives 15 coins".</summary>
        public static string Humanize(string method)
        {
            if (string.IsNullOrEmpty(method))
                return "(class)";
            var parts = method.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Join(" ", Hump.Split(p).Select(w => w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant())))
                .ToList();
            string text = string.Join(" → ", parts);
            return text.Length == 0 ? method : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>The assertions of a method body, shortened: Assert.That(coins, Is.EqualTo(15)) → "coins == 15".</summary>
        public static List<string> Assertions(string body)
        {
            var result = new List<string>();
            if (body == null)
                return result;
            foreach (Match m in AssertStart.Matches(body))
            {
                string kind = m.Groups[2].Value;
                if (kind == "Pass" || kind == "Inconclusive" || kind == "Ignore" || kind == "Warn")
                    continue;
                string inside = Balanced(body, m.Index + m.Length - 1);
                if (inside == null)
                    continue;
                string generic = m.Groups[3].Success ? m.Groups[3].Value.Trim('<', '>') : null;
                result.Add(Cap(Squash(Shorten(m.Groups[1].Value, kind, generic, Args(inside)))));
            }
            return result;
        }

        private static string Shorten(string cls, string kind, string generic, List<string> a)
        {
            string A(int i) => i < a.Count ? a[i] : "?";
            if (kind == "Throws" || kind == "ThrowsAsync" || kind == "Catch")
                return "throws " + (generic ?? "an exception");
            if (kind == "DoesNotThrow")
                return "does not throw";
            if (kind == "AreEqual") return A(1) + " == " + A(0);
            if (kind == "AreNotEqual") return A(1) + " != " + A(0);
            if (kind == "IsTrue" || kind == "True") return A(0);
            if (kind == "IsFalse" || kind == "False") return "!(" + A(0) + ")";
            if (kind == "IsNull" || kind == "Null") return A(0) + " is null";
            if (kind == "IsNotNull" || kind == "NotNull") return A(0) + " is not null";
            if (kind == "IsEmpty" || kind == "Empty") return A(0) + " is empty";
            if (kind == "Contains" && cls == "StringAssert") return A(1) + " contains " + A(0);
            if (kind == "Greater") return A(0) + " > " + A(1);
            if (kind == "Less") return A(0) + " < " + A(1);
            if (kind != "That" || a.Count < 2)
                return cls + "." + kind + "(" + string.Join(", ", a) + ")";
            return Constraint(A(0), A(1));
        }

        private static readonly (string prefix, string op)[] Ops =
        {
            ("Is.EqualTo(", " == "), ("Is.Not.EqualTo(", " != "), ("Is.GreaterThanOrEqualTo(", " >= "), ("Is.LessThanOrEqualTo(", " <= "),
            ("Is.GreaterThan(", " > "), ("Is.LessThan(", " < "), ("Is.AtLeast(", " >= "), ("Is.AtMost(", " <= "),
            ("Is.SameAs(", " is the same object as "), ("Is.InstanceOf<", " is "), ("Does.Contain(", " contains "), ("Contains.Item(", " contains "),
            ("Does.Not.Contain(", " does not contain "), ("Does.StartWith(", " starts with "), ("Has.Count.EqualTo(", ".Count == "),
            ("Has.Length.EqualTo(", ".Length == "), ("Is.EquivalentTo(", " has the same items as "), ("Is.InRange(", " in range "),
        };

        private static string Constraint(string actual, string c)
        {
            switch (c)
            {
                case "Is.True": return actual;
                case "Is.False": return "!(" + actual + ")";
                case "Is.Null": return actual + " is null";
                case "Is.Not.Null": return actual + " is not null";
                case "Is.Empty": return actual + " is empty";
                case "Is.Not.Empty": return actual + " is not empty";
                case "Is.Unique": return actual + " has no duplicates";
                case "Is.Ordered": return actual + " is ordered";
            }
            foreach (var (prefix, op) in Ops)
                if (c.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string arg = c.Substring(prefix.Length);
                    arg = prefix.EndsWith("<", StringComparison.Ordinal) ? arg.Split('>')[0] : Strip(arg);
                    return actual + op + arg;
                }
            return actual + ": " + c;
        }

        /// <summary>"15))" or "15).Within(1)" → "15" (+ the modifier, kept readable).</summary>
        private static string Strip(string arg)
        {
            int depth = 0;
            for (int i = 0; i < arg.Length; i++)
            {
                if (arg[i] == '(') depth++;
                else if (arg[i] == ')' && depth-- == 0)
                    return arg.Substring(0, i) + (i + 1 < arg.Length ? " " + arg.Substring(i + 1).TrimStart('.') : "");
            }
            return arg;
        }

        private static string Balanced(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '"')
                {
                    for (i++; i < text.Length && text[i] != '"'; i++)
                        if (text[i] == '\\') i++;
                    continue;
                }
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0)
                    return text.Substring(open + 1, i - open - 1);
            }
            return null;
        }

        /// <summary>Top-level comma split (not inside (), &lt;&gt;, [], {} or strings); the message argument stays but is rarely shown.</summary>
        private static List<string> Args(string inside)
        {
            var args = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < inside.Length; i++)
            {
                char ch = inside[i];
                if (ch == '"')
                {
                    for (i++; i < inside.Length && inside[i] != '"'; i++)
                        if (inside[i] == '\\') i++;
                    continue;
                }
                if (ch == '(' || ch == '[' || ch == '{' || ch == '<') depth++;
                else if (ch == ')' || ch == ']' || ch == '}' || ch == '>') depth--;
                else if (ch == ',' && depth == 0)
                {
                    args.Add(inside.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            args.Add(inside.Substring(start).Trim());
            return args;
        }

        /// <summary>The method from its name on (what precedes it depends on where the search started), whitespace-insensitive.</summary>
        private static string FromSignature(string text, string method)
        {
            int i = method == null ? -1 : text.IndexOf(method + "(", StringComparison.Ordinal);
            return Squash(i < 0 ? text : text.Substring(i));
        }

        private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();
        private static string Cap(string s) => s.Length <= MaxAssertLength ? s : s.Substring(0, MaxAssertLength - 1) + "…";

        // ------------------------------------------------------------------ requirements → entries

        /// <param name="baseText">file path → its text at the task's base commit (null = no comparison: Change stays null)</param>
        public static List<Entry> Build(string root, IEnumerable<string> requirements, Func<string, string> baseText)
        {
            var wanted = new HashSet<string>(requirements, StringComparer.Ordinal);
            var entries = new List<Entry>();
            foreach (var c in PlanRules.Covers(root).Where(c => wanted.Contains(c.Requirement)))
            {
                string now = SealStore.MethodText(root, c);
                var e = new Entry { Requirement = c.Requirement, TestKey = c.TestKey, Title = Humanize(c.Method), Asserts = Assertions(now) };
                if (baseText != null)
                {
                    string old = MethodIn(baseText(c.File), c);
                    e.Change = old == null ? "new" : FromSignature(old, c.Method) != FromSignature(now ?? "", c.Method) ? "changed" : null;
                }
                entries.Add(e);
            }
            return entries;
        }

        /// <summary>The method in an older version of its file: searched from the top (its line may have moved since).</summary>
        private static string MethodIn(string fileText, CoverEntry c)
        {
            if (fileText == null)
                return null;
            string code = SourceText.StripComments(fileText);
            return c.Method == null ? code : SealStore.MethodText(code, c.Method, 1);
        }

        /// <summary>One block per requirement: a line per test, then the count ("R-X: 3 test(s) (2 new, 1 changed), 7 assert(s)").</summary>
        public static List<string> Format(IEnumerable<Entry> entries, bool onlyChanged)
        {
            var lines = new List<string>();
            foreach (var g in entries.GroupBy(e => e.Requirement).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var shown = g.Where(e => !onlyChanged || e.Change != null).ToList();
                if (shown.Count == 0)
                    continue;
                foreach (var e in shown)
                    lines.Add("  " + (e.Change == null ? "" : "[" + e.Change + "] ") + e.Title + ": " +
                              (e.Asserts.Count == 0 ? "(no assertion found)" : string.Join("; ", e.Asserts)));
                int added = shown.Count(e => e.Change == "new"), changed = shown.Count(e => e.Change == "changed");
                string delta = added + changed == 0 ? "" : " (" + string.Join(", ", new[] { added > 0 ? added + " new" : null, changed > 0 ? changed + " changed" : null }.Where(x => x != null)) + ")";
                lines.Insert(lines.Count - shown.Count, g.Key + ": " + shown.Count + " test(s)" + delta + ", " + shown.Sum(e => e.Asserts.Count) + " assert(s)");
            }
            return lines;
        }

        // ------------------------------------------------------------------ task close and CLI

        /// <summary>The tests this task added or changed for its requirements (base = the last commit before the task started).</summary>
        public static List<string> ForTask(string root, AgentTask task)
        {
            if (task == null || task.Requirements.Count == 0)
                return new List<string>();
            string baseCommit = BaseCommit(root, task);
            Func<string, string> baseText = baseCommit == null ? (Func<string, string>)(_ => null) : f =>
            {
                var (code, output) = AgentGit.Run(root, "show \"" + baseCommit + ":" + f + "\"");
                return code == 0 ? output : null;
            };
            return Format(Build(root, task.Requirements.Keys, baseText), onlyChanged: true);
        }

        private static string BaseCommit(string root, AgentTask task)
        {
            if (!DateTime.TryParse(task.CreatedTs, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                return null;
            string sha = AgentGit.Run(root, "rev-list -1 --before=\"" + created.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\" HEAD").output.Trim();
            return sha.Length == 0 ? null : sha;
        }

        /// <summary>`tests summary [R-ID|F-ID]`: every requirement (or one feature's, or one) with what its tests check.</summary>
        public static int Cli(string root, string target)
        {
            var plan = PlanParser.Load(root);
            if (plan == null)
            {
                Console.Error.WriteLine("no plan");
                return 1;
            }
            var reqs = plan.AllRequirements.Where(r => target == null || r.Id == target || r.Feature == target).Select(r => r.Id).ToList();
            if (reqs.Count == 0)
            {
                Console.Error.WriteLine((target ?? "the plan") + ": no requirement");
                return 1;
            }
            var lines = Format(Build(root, reqs, null), onlyChanged: false);
            var sb = new StringBuilder();
            foreach (var l in lines)
                sb.AppendLine(l);
            Console.Write(sb.Length == 0 ? "no [Covers] test for " + (target ?? "the plan") + "\n" : sb.ToString());
            return 0;
        }
    }
}
