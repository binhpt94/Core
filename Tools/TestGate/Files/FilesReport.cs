using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;
using TestGate.AgentControl;

namespace TestGate.Files
{
    public sealed class LooseFile
    {
        public string Path { get; set; }
        /// <summary>Who made it, from the event log: "session/agent", or "unknown" (human, IDE, another tool, before recording).</summary>
        public string Origin { get; set; }
        public string Session { get; set; }
        /// <summary>The creator's session ended without a clean Stop (crash, killed, still running).</summary>
        public bool Orphan { get; set; }
        /// <summary>Content still exactly what the agent wrote (nobody touched it since).</summary>
        public bool Unchanged { get; set; }
        public List<string> Problems { get; set; } = new List<string>();
        public long Bytes { get; set; }

        /// <summary>Deliverables waiting for a human (proposals): never cleanup material, whoever wrote them.</summary>
        public bool ForHuman => Path.StartsWith("Docs/Plan/Inbox/", StringComparison.Ordinal);

        /// <summary>Safe to delete without asking: never committed + made by an agent + untouched since + not a deliverable for a human.</summary>
        public bool SafeToDelete => Origin != "unknown" && Unchanged && !ForHuman;
    }

    public sealed class UnusedAsset
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string Why { get; set; }
    }

    public sealed class DiskUse
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public bool Regenerable { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Everything about "which files are where and why", DERIVED (no registry to drift): git (official vs untracked),
    /// the event log (who wrote it, was the session finished, is the content unchanged), paths (layout rules), .meta + YAML (GUID graph).
    /// </summary>
    public static class FilesReport
    {
        // ------------------------------------------------------------------ loose (untracked) files

        /// <param name="events">the event log if the caller already read it (it is read once per command)</param>
        public static List<LooseFile> Loose(string root, IReadOnlyList<AgentEvent> events = null)
        {
            events = events ?? EventLog.For(root).ReadAll();
            var lastWrite = new Dictionary<string, AgentEvent>(StringComparer.Ordinal);
            foreach (var e in events.Where(e => e.Get("path") != null && (e.Type == EventTypes.FileModified || e.Type == EventTypes.UnobservedChange || e.Type == EventTypes.FileBaseline)))
                lastWrite[e.Get("path")] = e;
            var lastStop = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var e in events.Where(e => e.Type == EventTypes.StopAllowed && e.Session != null))
                lastStop[e.Session] = e.Seq;
            // A session is OVER only if another session started after it wrote (evidence), or it stopped cleanly afterwards.
            // No evidence (hooks installed mid-session, log deleted) = not an orphan: never risk deleting work in progress.
            var starts = events.Where(e => e.Type == EventTypes.SessionStarted && e.Session != null).Select(e => (e.Seq, e.Session)).ToList();

            var result = new List<LooseFile>();
            foreach (var path in Untracked(root))
            {
                string full = System.IO.Path.Combine(root, path);
                var f = new LooseFile { Path = path, Origin = "unknown", Bytes = File.Exists(full) ? new FileInfo(full).Length : 0 };
                if (lastWrite.TryGetValue(path, out var e) && e.Type == EventTypes.FileModified && e.Session != null && e.Session != "human")
                {
                    f.Origin = e.Session + "/" + (e.Agent ?? "main");
                    f.Session = e.Session;
                    f.Unchanged = File.Exists(full) && WorkSnapshot.HashFile(full) == e.Get("after");
                    bool stoppedAfter = lastStop.TryGetValue(e.Session, out var s) && s > e.Seq;
                    bool succeeded = starts.Any(st => st.Seq > e.Seq && st.Session != e.Session);
                    f.Orphan = !stoppedAfter && succeeded && !f.ForHuman;
                }
                f.Problems.AddRange(FileRules.CheckPath(path).Select(v => v.RuleId + " " + v.Message));
                result.Add(f);
            }
            // A .meta Unity generated for an agent's file follows its asset.
            foreach (var meta in result.Where(x => x.Path.EndsWith(".meta", StringComparison.Ordinal) && x.Origin == "unknown").ToList())
            {
                var asset = result.FirstOrDefault(x => x.Path == meta.Path.Substring(0, meta.Path.Length - 5));
                if (asset != null && asset.Origin != "unknown")
                {
                    meta.Origin = asset.Origin + " (Unity .meta)";
                    meta.Session = asset.Session;
                    meta.Orphan = asset.Orphan;
                    meta.Unchanged = asset.Unchanged;
                }
            }
            return result.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        }

        public static IEnumerable<string> Untracked(string root)
        {
            var (code, output) = FileRules.Git(root, "ls-files --others --exclude-standard");
            return code != 0 ? Enumerable.Empty<string>() : output.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !WorkSnapshot.Ignored(x));
        }

        // ------------------------------------------------------------------ unused assets (GUID graph)

        private static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-f]{32})");
        private static readonly Regex AssetPathInCode = new Regex(@"""(Assets/[^""]+\.[A-Za-z0-9]+)""");

        /// <summary>
        /// Private feature assets nothing reaches from the entry points: scenes, Assets/Public/**, ProjectSettings, asset paths written
        /// in code (recipes). Report only: assets loaded by string (Addressables) would look unused, and deleting is a human call.
        /// </summary>
        public static List<UnusedAsset> Unused(string root)
        {
            var guids = new AssetRules(root).GuidIndex; // guid → path
            var byPath = guids.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
            string assets = System.IO.Path.Combine(root, "UnityProject", "Assets");
            if (!Directory.Exists(assets))
                return new List<UnusedAsset>();
            var all = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)
                .Select(f => PathInfo.Normalize(f.Substring(root.Length))).Where(p => !p.EndsWith(".meta", StringComparison.Ordinal)).ToList();

            var roots = all.Where(p => p.EndsWith(".unity", StringComparison.Ordinal) || p.Contains("/Assets/Public/")).ToList();
            string settings = System.IO.Path.Combine(root, "UnityProject", "ProjectSettings");
            if (Directory.Exists(settings))
                roots.AddRange(Directory.GetFiles(settings, "*.asset").Select(f => PathInfo.Normalize(f.Substring(root.Length))));
            foreach (var cs in all.Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
                foreach (Match m in AssetPathInCode.Matches(File.ReadAllText(System.IO.Path.Combine(root, cs))))
                    roots.Add(Layout.FromUnity(m.Groups[1].Value)); // code names Unity paths ("Assets/...")

            var reached = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(roots.Distinct());
            while (queue.Count > 0)
            {
                string p = queue.Dequeue();
                if (!reached.Add(p) || !AssetRules.IsYaml(p))
                    continue;
                string full = System.IO.Path.Combine(root, p);
                if (!File.Exists(full))
                    continue;
                foreach (Match m in GuidRef.Matches(File.ReadAllText(full)))
                    if (guids.TryGetValue(m.Groups[1].Value, out var target) && !reached.Contains(target))
                        queue.Enqueue(target);
            }

            return all.Where(p => p.Contains("/Assets/Private/") && !p.EndsWith(".gitkeep", StringComparison.Ordinal) && !reached.Contains(p))
                .Select(p => new UnusedAsset
                {
                    Path = p, Bytes = new FileInfo(System.IO.Path.Combine(root, p)).Length,
                    Why = byPath.ContainsKey(p) ? "nothing references it from scenes, Assets/Public, settings or code" : "no .meta: references UNKNOWN (open in Unity first)"
                })
                .OrderByDescending(u => u.Bytes).ToList();
        }

        // ------------------------------------------------------------------ disk use

        private static readonly (string Path, bool Regenerable, string Note)[] Known =
        {
            ("UnityProject/Library", true, "Unity cache: regenerated on open (slow). Only `files clean --library`."),
            ("UnityProject/Temp", true, "Unity temp"), ("UnityProject/Logs", true, "Unity logs"), ("Dashboard/Data", true, "gate results (dashboard reads them)"),
            (".scratch", true, "scratch work (gitignored)"), (".agent", false, "event log + task: keep (evidence)"),
            ("Tools/LogicTests/bin", true, "dotnet build"), ("Tools/LogicTests/obj", true, "dotnet build"),
            ("UnityProject/Assets", false, "the game"), ("UnityProject/Packages", false, "package manifest (+ embedded packages)")
        };

        public static List<DiskUse> Du(string root)
        {
            var list = Known.Select(k => new DiskUse { Path = k.Path, Regenerable = k.Regenerable, Note = k.Note, Bytes = Size(System.IO.Path.Combine(root, k.Path)) })
                .Where(d => d.Bytes > 0).ToList();
            var (code, wt) = FileRules.Git(root, "worktree list --porcelain");
            if (code == 0)
                foreach (var line in wt.Split('\n').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Skip(1))
                {
                    string p = line.Substring(9).Trim();
                    list.Add(new DiskUse { Path = p, Bytes = Size(p), Regenerable = false, Note = "git worktree (parallel agent): `git worktree remove` when its task is closed" });
                }
            return list.OrderByDescending(d => d.Bytes).ToList();
        }

        public static long Size(string path)
        {
            try
            {
                if (File.Exists(path))
                    return new FileInfo(path).Length;
                return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        public static string Human(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" :
            bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0") + " MB" :
            bytes >= 1L << 10 ? (bytes / 1024.0).ToString("0") + " KB" : bytes + " B";
    }
}
