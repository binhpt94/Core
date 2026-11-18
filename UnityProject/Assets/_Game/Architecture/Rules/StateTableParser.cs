using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Reads `Transition&lt;TState&gt;` tables for the state diagrams. Fail-closed by construction:
    /// - a table is a declaration typed IReadOnlyList&lt;Transition&lt;E&gt;&gt; or Transition&lt;E&gt;[] with an initializer;
    /// - EVERY element of the initializer must be a row it understands
    ///   (`new Transition&lt;E&gt;(E.A, trigger, E.B)` or target-typed `new(E.A, trigger, E.B)`), else ARCH090;
    /// - any `new Transition&lt;E&gt;(` outside a recognised table is ARCH090 too.
    /// A runtime reflection test (GraphConsistencyTests) compares the parsed rows with the real tables as a backstop.
    /// </summary>
    public static class StateTableParser
    {
        private static readonly Regex TableDecl = new Regex(
            @"(?:IReadOnlyList<\s*Transition<(\w+)>\s*>|Transition<(\w+)>\s*\[\s*\])\s+(\w+)\s*(?:\{\s*get;\s*\}\s*)?=\s*new\b[^{;]*\{");
        private static readonly Regex Row = new Regex(
            @"^new(?:\s+Transition<\s*(\w+)\s*>)?\s*\(\s*(\w+)\.(\w+)\s*,\s*(""[^""]*""|\w+)\s*,\s*(\w+)\.(\w+)\s*\)$", RegexOptions.Singleline);
        private static readonly Regex AnyTransitionNew = new Regex(@"new\s+Transition<\s*\w+\s*>\s*\(");
        private static readonly Regex ConstString = new Regex(@"const\s+string\s+(\w+)\s*=\s*""([^""]*)""");
        private static readonly Regex ClassDecl = new Regex(@"\b(?:class|struct)\s+(\w+)");

        public static void Parse(PathInfo file, string code, List<ArchitectureGraph.StateMachine> machines, List<RuleViolation> errors)
        {
            if (code.IndexOf("Transition<", StringComparison.Ordinal) < 0)
                return;

            var consts = ConstString.Matches(code).Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
            var covered = new List<(int Start, int End)>();

            foreach (Match decl in TableDecl.Matches(code))
            {
                string stateEnum = decl.Groups[1].Success ? decl.Groups[1].Value : decl.Groups[2].Value;
                int open = decl.Index + decl.Length - 1;
                int close = MatchingBrace(code, open);
                if (close < 0)
                {
                    errors.Add(Error(file, code, decl.Index, "state table '" + decl.Groups[3].Value + "': unbalanced braces"));
                    continue;
                }
                covered.Add((decl.Index, close));

                string owner = ClassDecl.Matches(code.Substring(0, decl.Index)).Cast<Match>().Select(c => c.Groups[1].Value).LastOrDefault() ?? "?";
                var sm = new ArchitectureGraph.StateMachine { Owner = owner, Enum = stateEnum, Feature = file.Feature, Table = decl.Groups[3].Value };

                int offset = open + 1;
                foreach (var element in SplitTopLevel(code.Substring(open + 1, close - open - 1)))
                {
                    string text = element.Text.Trim();
                    int at = offset + element.Start;
                    if (text.Length == 0)
                        continue;
                    var m = Row.Match(text);
                    if (!m.Success || (m.Groups[1].Success && m.Groups[1].Value != stateEnum) || m.Groups[2].Value != stateEnum || m.Groups[5].Value != stateEnum)
                    {
                        errors.Add(Error(file, code, at, "state table row not understood by the diagram generator: '" + Short(text) +
                            "'. Write rows as new Transition<" + stateEnum + ">(" + stateEnum + ".From, Trigger, " + stateEnum + ".To)"));
                        continue;
                    }
                    string trigger = m.Groups[4].Value;
                    if (trigger.StartsWith("\"", StringComparison.Ordinal))
                        trigger = trigger.Trim('"');
                    else if (consts.TryGetValue(trigger, out var value))
                        trigger = value;
                    else
                    {
                        errors.Add(Error(file, code, at, "trigger '" + trigger + "' must be a string literal or a const string in the same file"));
                        continue;
                    }
                    sm.Transitions.Add((m.Groups[3].Value, trigger, m.Groups[6].Value));
                }

                if (sm.Transitions.Count > 0)
                    machines.Add(sm);
            }

            foreach (Match stray in AnyTransitionNew.Matches(code))
                if (!covered.Any(c => stray.Index > c.Start && stray.Index < c.End))
                    errors.Add(Error(file, code, stray.Index, "Transition created outside a recognised table (IReadOnlyList<Transition<E>> X { get; } = new[] { ... }); the diagram would miss it"));
        }

        private static int MatchingBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Splits on commas that are not nested in (), {}, [] or strings.</summary>
        private static IEnumerable<(string Text, int Start)> SplitTopLevel(string s)
        {
            int depth = 0, start = 0;
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == '(' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return (s.Substring(start, i - start), start);
                    start = i + 1;
                }
            }
            yield return (s.Substring(start), start);
        }

        private static string Short(string s)
        {
            s = Regex.Replace(s, @"\s+", " ");
            return s.Length > 70 ? s.Substring(0, 70) + "…" : s;
        }

        private static RuleViolation Error(PathInfo file, string code, int index, string message) =>
            new RuleViolation("ARCH090", file.Path, SourceText.LineOf(code, index), message);
    }
}
