using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>One requirement's sealed tests, folded from its feature's seal log.</summary>
    public sealed class SealState
    {
        public string Requirement { get; set; }
        public string Feature { get; set; }
        public string Hash { get; set; }             // SealStore.TestsHash of its covering test methods at seal time
        public string RequirementHash { get; set; }  // wording at seal time: reworded = the seal no longer counts
        public string Author { get; set; }           // session that wrote and sealed the tests ("legacy" = grandfathered)
        public string Red { get; set; }              // "<failed>/<total>" | "pending" (Unity-only tests) | "legacy"
        public string Commit { get; set; }           // code the tests were red against (re-seals are checked against it)
        public string PreviousCommit { get; set; }
        public bool Void { get; set; }               // unsealed, or the arbiter ruled "fix": must be sealed again
        public bool OpenDispute { get; set; }
        public string DisputedBy { get; set; }
        public int DisputeCount { get; set; }
        public bool Ask { get; set; }                // the arbiter (or the 3rd dispute) asks the owner to reword the requirement
        public bool Suspect { get; set; }            // its author's seals were fixed too often: re-checked by an arbiter
        public bool Reviewed { get; set; }           // an arbiter ruled "keep" on this seal: not suspect any more
        public bool Verified { get; set; }           // its sealed tests passed once: red again = regression, not pending work
        public List<string> History { get; set; } = new List<string>();
    }

    /// <summary>
    /// Docs/Plan/Seals/&lt;F-ID&gt;.txt: append-only log, one file per plan feature (parallel features never touch the same file).
    ///   seal R-X tests=&lt;h&gt; req=&lt;h&gt; author=&lt;session&gt; red=&lt;f&gt;/&lt;n&gt; commit=&lt;sha&gt; ts=&lt;t&gt;
    ///   dispute R-X by=&lt;session&gt; ts=&lt;t&gt; reason=&lt;text&gt;
    ///   rule R-X keep|fix|ask by=&lt;session&gt; ts=&lt;t&gt; reason=&lt;text&gt;
    ///   unseal R-X by=owner ts=&lt;t&gt; reason=&lt;text&gt;
    ///   verified R-X ts=&lt;t&gt;                 (the gate: its sealed tests passed; tracked, so CI knows it too)
    /// Written only by `test-gate.sh tests …` (each write is a SEAL_WRITTEN event with the file's hash); a hand edit is a breach.
    /// </summary>
    public static class SealStore
    {
        public const string Dir = "Docs/Plan/Seals";
        public const string WrittenEvent = "SEAL_WRITTEN";
        public const int SuspectWindow = 5, SuspectFixes = 2, AskAfterDisputes = 3;
        private static readonly Regex Field = new Regex(@"(\w+)=(\S+)");

        public static string FileOf(string feature) => Dir + "/" + feature + ".txt";

        public static Dictionary<string, SealState> Load(string root)
        {
            var states = new Dictionary<string, SealState>(StringComparer.Ordinal);
            var fixes = new List<(string author, string req)>();
            var seals = new List<(string author, string ts, string req)>();
            string dir = Path.Combine(root, Dir);
            if (!Directory.Exists(dir))
                return states;
            foreach (var file in Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
                foreach (var line in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
                    Apply(states, Path.GetFileNameWithoutExtension(file), line, fixes, seals);
            MarkSuspects(states, fixes, seals);
            return states;
        }

        private static void Apply(Dictionary<string, SealState> states, string feature, string line, List<(string, string)> fixes, List<(string, string, string)> seals)
        {
            var parts = line.Split(new[] { ' ' }, 3);
            if (parts.Length < 2)
                return;
            string verb = parts[0], req = parts[1], rest = parts.Length > 2 ? parts[2] : "";
            int r = rest.IndexOf("reason=", StringComparison.Ordinal);
            string reason = r >= 0 ? rest.Substring(r + 7) : null;
            var f = Field.Matches(r >= 0 ? rest.Substring(0, r) : rest).Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            string Get(string k) => f.TryGetValue(k, out var v) ? v : null;
            if (!states.TryGetValue(req, out var s))
                states[req] = s = new SealState { Requirement = req, Feature = feature, Void = true };
            s.History.Add(line);
            switch (verb)
            {
                case "seal":
                    if (s.Commit != null)
                        s.PreviousCommit = s.Commit;
                    s.Hash = Get("tests"); s.RequirementHash = Get("req"); s.Author = Get("author"); s.Red = Get("red"); s.Commit = Get("commit");
                    s.Void = false; s.OpenDispute = false; s.Ask = false; s.Suspect = false; s.Reviewed = false; s.Verified = false;
                    seals.Add((s.Author, Get("ts") ?? "", req));
                    break;
                case "dispute":
                    s.OpenDispute = true; s.DisputedBy = Get("by"); s.DisputeCount++;
                    break;
                case "rule":
                    string ruling = rest.Split(' ')[0];
                    if (ruling == "keep") { s.OpenDispute = false; s.Reviewed = true; }
                    else if (ruling == "fix") { s.OpenDispute = false; s.Void = true; s.PreviousCommit = s.Commit; fixes.Add((s.Author, req)); }
                    else if (ruling == "ask") s.Ask = true;
                    break;
                case "verified":
                    s.Verified = true;
                    break;
                case "unseal":
                    s.Void = true; s.OpenDispute = false; s.Ask = false; s.PreviousCommit = s.Commit ?? s.PreviousCommit;
                    break;
            }
        }

        /// <summary>
        /// An author with ≥ 2 arbiter "fix" rulings among their last 5 seals: their other live seals are re-checked by an arbiter
        /// (status DISPUTED, lane "Phân xử") before an impl session runs into them one by one. A "keep" ruling clears it.
        /// </summary>
        private static void MarkSuspects(Dictionary<string, SealState> states, List<(string author, string req)> fixes, List<(string author, string ts, string req)> seals)
        {
            foreach (var g in seals.Where(x => x.author != null && x.author != "legacy").GroupBy(x => x.author))
            {
                var window = new HashSet<string>(g.OrderByDescending(x => x.ts, StringComparer.Ordinal).Take(SuspectWindow).Select(x => x.req), StringComparer.Ordinal);
                if (fixes.Count(x => x.author == g.Key && window.Contains(x.req)) < SuspectFixes)
                    continue;
                foreach (var s in states.Values.Where(s => s.Author == g.Key && !s.Void && !s.OpenDispute && !s.Reviewed))
                    s.Suspect = true;
            }
        }

        /// <summary>Appends one line to a feature's seal log and records the write (the only legitimate way the file changes).</summary>
        public static void Append(string root, string feature, string line)
        {
            string full = Path.Combine(root, FileOf(feature));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.AppendAllText(full, line.Replace("\r", " ").Replace("\n", " ") + "\n");
            Recorder.Emit(Recorder.New(WrittenEvent, "tests-cli").With("path", FileOf(feature)).With("line", line.Length > 300 ? line.Substring(0, 300) : line)
                .With("after", WorkSnapshot.HashFile(full)));
        }

        /// <summary>Is this content of a seal file one the gate wrote? (Bash running `tests …` changes it; that change is allowed.)</summary>
        public static bool IsGateWrite(string root, string rel, string state) =>
            rel != null && rel.StartsWith(Dir + "/", StringComparison.Ordinal) &&
            EventLog.For(root).ReadAll().Any(e => e.Type == WrittenEvent && e.Get("path") == rel && e.Get("after") == state);

        // ------------------------------------------------------------------ what exactly was sealed

        /// <summary>Hash of the covering test METHODS (not whole files: another requirement's tests in the same file don't tamper this one).</summary>
        public static string TestsHash(string root, IEnumerable<CoverEntry> covers) =>
            PlanText.Hash(string.Join("|", covers.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Method, StringComparer.Ordinal)
                .Select(c => c.File + ":" + c.Method + ":" + PlanText.Hash(MethodText(root, c) ?? "-"))));

        public static string MethodText(string root, CoverEntry c)
        {
            string full = Path.Combine(root, c.File);
            if (!File.Exists(full))
                return null;
            string code = SourceText.StripComments(File.ReadAllText(full));
            return c.Method == null ? code : MethodText(code, c.Method, c.Line);
        }

        /// <summary>From the [Covers] line: the method's signature and its body (brace-matched, or an expression body up to ';').</summary>
        public static string MethodText(string code, string method, int line)
        {
            int start = IndexOfLine(code, Math.Max(1, line));
            int sig = code.IndexOf(method + "(", start, StringComparison.Ordinal);
            if (sig < 0)
                return null;
            int brace = code.IndexOf('{', sig), arrow = code.IndexOf("=>", sig, StringComparison.Ordinal);
            if (arrow >= 0 && (brace < 0 || arrow < brace))
            {
                int semi = code.IndexOf(';', arrow);
                return code.Substring(start, (semi < 0 ? code.Length : semi + 1) - start);
            }
            if (brace < 0)
                return null;
            int depth = 0;
            for (int i = brace; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(start, i + 1 - start);
            }
            return code.Substring(start);
        }

        private static int IndexOfLine(string code, int line)
        {
            int idx = 0;
            for (int l = 1; l < line && idx >= 0; l++)
                idx = code.IndexOf('\n', idx) + 1;
            return Math.Max(0, Math.Min(idx, code.Length));
        }
    }
}
