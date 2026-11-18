using System;
using System.Collections.Generic;
using System.Linq;


namespace TestGate.AgentControl
{
    /// <summary>
    /// Which files a change touches, read from git's STDOUT only: git prints warnings (e.g. "LF will be replaced by CRLF")
    /// on stderr, and a warning read as a path is an "unknown file role" that turns every plan into a full run.
    /// </summary>
    public static class GitChanges
    {
        /// <summary>Committed since <paramref name="baseRef"/> + staged + unstaged + untracked (no .meta). Null (= run everything) without git.</summary>
        public static List<string> Files(string root, string baseRef, out string fullReason)
        {
            fullReason = null;
            if (AgentGit.Run(root, "rev-parse --is-inside-work-tree").code != 0)
            {
                fullReason = "not a git repository";
                return null;
            }

            if (string.IsNullOrEmpty(baseRef))
                baseRef = AgentGit.Run(root, "rev-parse --verify --quiet origin/main").code == 0 ? "origin/main" : "HEAD";

            var files = new SortedSet<string>(StringComparer.Ordinal);
            var (code, committed) = AgentGit.Run(root, "diff --relative --name-only " + baseRef + "...HEAD");
            if (code != 0 && baseRef != "HEAD")
            {
                fullReason = "cannot diff against " + baseRef;
                return null;
            }
            files.UnionWith(Lines(committed));
            files.UnionWith(Lines(AgentGit.Run(root, "diff --relative --name-only HEAD").output));          // staged + unstaged
            files.UnionWith(Lines(AgentGit.Run(root, "ls-files --others --exclude-standard").output));     // new files
            return files.Where(f => !f.EndsWith(".meta", StringComparison.Ordinal)).ToList();
        }

        /// <summary>Staged files that still exist (added, copied, modified, renamed).</summary>
        public static List<string> Staged(string root) =>
            Lines(AgentGit.Run(root, "diff --cached --relative --name-only --diff-filter=ACMR").output).ToList();

        private static IEnumerable<string> Lines(string output) =>
            output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
    }
}
