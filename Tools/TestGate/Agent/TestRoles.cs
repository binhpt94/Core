using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Separation of duties for requirement tests (a task's --role):
    ///   tests    writes Features/F/Tests/** and a skeleton (new members with default bodies) so the tests compile; seals them
    ///   impl     writes the feature's code; never its tests (a test it thinks wrong is disputed, never edited)
    ///   arbiter  settles disputes: keeps the test, fixes it (and seals again) or asks the owner; writes like `tests`
    ///   (none)   old behaviour: writes both, but its requirements can't be VERIFIED without a seal by another session
    /// </summary>
    public static class TestRoles
    {
        public const string Tests = "tests";
        public const string Impl = "impl";
        public const string Arbiter = "arbiter";
        public static readonly string[] All = { Tests, Impl, Arbiter };

        public static bool WritesTests(string role) => role != Impl;
        public static bool WritesCode(string role) => role != Tests && role != Arbiter;

        /// <summary>Two open tasks on overlapping modules may run together only when they write disjoint folders.</summary>
        public static bool Conflict(string a, string b) => WritesTests(a) && WritesTests(b) || WritesCode(a) && WritesCode(b);

        public static bool IsTestPath(string featureRoot, string path) => path.StartsWith(featureRoot + "Tests/", StringComparison.Ordinal);

        public static bool IsSkeletonPath(string featureRoot, string path) =>
            path.EndsWith(".cs", StringComparison.Ordinal) &&
            (path.StartsWith(featureRoot + "Logic/", StringComparison.Ordinal) || path.StartsWith(featureRoot + "Contracts/", StringComparison.Ordinal));

        /// <summary>Scope inside one feature folder, by role (called by ScopePolicy once the path is known to be in feature F).</summary>
        public static ScopeDecision Check(string role, string featureRoot, string feature, string path)
        {
            if (role == Impl && IsTestPath(featureRoot, path))
                return ScopeDecision.Deny(path + ": tests of this feature are written and sealed by another session (role tests). " +
                                          "A test you think is wrong: ./Tools/test-gate.sh tests dispute R-ID \"<why + evidence>\" and work on another requirement");
            if (role == Tests || role == Arbiter)
            {
                if (IsTestPath(featureRoot, path))
                    return ScopeDecision.Allow("tests of feature " + feature + " (role " + role + ")");
                if (IsSkeletonPath(featureRoot, path))
                    return ScopeDecision.Allow("skeleton in feature " + feature + " (role " + role + "; checked: default bodies only)");
                return ScopeDecision.Deny(path + ": role " + role + " writes only Tests/ and a skeleton in Logic/ or Contracts/ (the impl session writes the code)");
            }
            return ScopeDecision.Allow("inside feature " + feature + (role == null ? "" : " (role " + role + ")"));
        }

        // ------------------------------------------------------------------ skeleton

        private static readonly Regex Statement = new Regex(@"^(return|throw|yield|await|new|var|if|else|for|foreach|while|do|switch|case|break|continue|goto|lock|using\s*\()\b");
        private static readonly string[] Allowed =
        {
            @"^$", @"^//", @"^using\s+[\w\.]+(\s*=\s*[\w\.<>]+)?;$", @"^namespace\s+[\w\.]+\s*;?$", @"^[{}];?$", @"^\[.*\]$",
            @"^return default;$", @"^=> default;$", @"^\w+\s*(=\s*\d+\s*)?,?$",
            @"^((public|internal|private|protected|static|sealed|abstract|partial|readonly)\s+)*(class|interface|struct|enum|record)\s+\w+[^=;]*$",
            @"^((public|internal|private|protected|static|sealed|abstract|virtual|override|readonly|async|partial)\s+)*[\w<>\[\],\.\?]+(\s*<[^>]*>)?\s+\w+\s*(<[^>]*>)?" +
                @"(\([^=]*\))?\s*(\{\s*get;\s*((private|internal|protected)\s+)?(set|init)?;?\s*\}|=>\s*default;|;|\{\s*\}|\{)?$"
        };
        private static readonly Regex[] AllowedRx = Allowed.Select(p => new Regex(p)).ToArray();

        /// <summary>
        /// What a tests/arbiter session added to Logic/ or Contracts/ beyond a skeleton: every added line must be a declaration, a default
        /// body (<c>=&gt; default;</c>, <c>return default;</c>, <c>{ }</c>), an attribute, a comment or a brace; nothing may be removed.
        /// Default bodies (not throw) make an unmet test fail on its ASSERTION, which is what "red first" counts.
        /// </summary>
        public static List<string> SkeletonViolations(string before, string after)
        {
            var oldLines = Lines(before);
            var newLines = Lines(after);
            var pool = newLines.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
            var problems = new List<string>();
            foreach (var l in oldLines)
            {
                if (pool.TryGetValue(l, out int n) && n > 0)
                    pool[l] = n - 1;
                else if (l.Length > 0)
                    problems.Add("removed existing code: " + l);
            }
            var keep = oldLines.GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
            foreach (var l in newLines)
            {
                if (keep.TryGetValue(l, out int n) && n > 0)
                {
                    keep[l] = n - 1;
                    continue;
                }
                if (Statement.IsMatch(l) && l != "return default;" || !AllowedRx.Any(r => r.IsMatch(l)))
                    problems.Add("not a skeleton line (declarations and default bodies only): " + l);
            }
            return problems;
        }

        private static List<string> Lines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}
