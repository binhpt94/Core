using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TestGate.AgentControl
{
    public static class AgentGit
    {
        public static (int code, string output) Run(string root, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return (p.ExitCode, output);
                }
            }
            catch (Exception)
            {
                return (-1, "");
            }
        }
    }

    /// <summary>
    /// What the disk really looks like: every file that differs from HEAD (tracked or untracked) → short content hash.
    /// Taken before and after each Bash command, so edits made with sed/echo/scripts are OBSERVED even though they
    /// never went through the Edit tool. Reconciled at Stop against the event log: anything not explained is UNOBSERVED.
    /// </summary>
    public static class WorkSnapshot
    {
        public const string Clean = "clean";
        public const string Deleted = "deleted";

        /// <summary>Paths the observer never reports (its own state, build output).</summary>
        public static bool Ignored(string path) =>
            path.StartsWith(".agent/", StringComparison.Ordinal) || path.StartsWith("Dashboard/Data/", StringComparison.Ordinal) || path.StartsWith(".scratch/", StringComparison.Ordinal)
            || path.Contains("/bin/") || path.Contains("/obj/");

        public static SortedDictionary<string, string> Take(string root)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var (code, output) = AgentGit.Run(root, "status --porcelain=v1 -z -uall");
            if (code != 0)
                return map;
            var parts = output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                string entry = parts[i];
                if (entry.Length < 4)
                    continue;
                string status = entry.Substring(0, 2);
                string path = entry.Substring(3);
                if (status[0] == 'R' || status[0] == 'C')
                    i++; // the next NUL-separated field is the old path
                if (Ignored(path))
                    continue;
                string full = Path.Combine(root, path);
                map[path] = File.Exists(full) ? HashFile(full) : Deleted;
            }
            return map;
        }

        public static string HashFile(string full)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs), 0, 8).Replace("-", "").ToLowerInvariant();
            }
            catch (IOException)
            {
                return "unreadable";
            }
        }

        /// <summary>State of one path in a snapshot (Clean = same as HEAD).</summary>
        public static string StateOf(IDictionary<string, string> snapshot, string path) =>
            snapshot.TryGetValue(path, out var h) ? h : Clean;

        /// <summary>Paths whose state differs between two snapshots, with before/after states.</summary>
        public static List<(string Path, string Before, string After)> Diff(IDictionary<string, string> before, IDictionary<string, string> after) =>
            before.Keys.Union(after.Keys).Distinct().OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (p, StateOf(before, p), StateOf(after, p)))
                .Where(x => x.Item2 != x.Item3)
                .ToList();

        public static string ToJson(IDictionary<string, string> snapshot) => JsonSerializer.Serialize(snapshot);

        public static SortedDictionary<string, string> FromJson(string json)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json))
                return map;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            foreach (var kv in parsed)
                map[kv.Key] = kv.Value;
            return map;
        }

        /// <summary>
        /// The state the event log can explain for each path: the latest FILE_MODIFIED / FILE_BASELINE / UNOBSERVED_CHANGE.
        /// Reconcile = current disk vs this; every difference is a change no trusted observer saw.
        /// </summary>
        public static Dictionary<string, string> Explained(IEnumerable<AgentEvent> events)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in events)
                if ((e.Type == EventTypes.FileModified || e.Type == EventTypes.FileBaseline || e.Type == EventTypes.UnobservedChange || e.Type == EventTypes.FileCleaned) && e.Get("path") != null)
                    map[e.Get("path")] = e.Get("after") ?? Clean;
            return map;
        }

        public static List<(string Path, string Explained, string Actual)> Unexplained(IDictionary<string, string> current, Dictionary<string, string> explained)
        {
            var result = new List<(string, string, string)>();
            foreach (var path in current.Keys.Union(explained.Keys).Distinct().OrderBy(p => p, StringComparer.Ordinal))
            {
                string actual = StateOf(current, path);
                string known = explained.TryGetValue(path, out var k) ? k : Clean;
                if (actual != known)
                    result.Add((path, known, actual));
            }
            return result;
        }
    }
}
