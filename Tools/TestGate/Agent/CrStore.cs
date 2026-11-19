using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Game.Architecture.Rules;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Writes the CR logs (format and folding: <see cref="CrLog"/> in the rule engine, so the plan sees approved CRs as requirements).
    /// Only the gate writes them: each write is a CR_WRITTEN event with the file's hash; a hand edit is a breach (Docs/Plan/CRs is protected).
    /// </summary>
    public static class CrStore
    {
        public const string WrittenEvent = "CR_WRITTEN";

        public static Dictionary<string, CrEntry> Load(string root) => CrLog.Load(root);

        /// <summary>Appends to the tracked log (travels with the commit) and to the shared mirror (other worktrees see it at once).</summary>
        public static void Append(string root, string target, string line, bool mirror = true)
        {
            line = line.Replace("\r", " ").Replace("\n", " ").Trim();
            string rel = CrLog.FileOf(target);
            string full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.AppendAllText(full, line + "\n");
            Recorder.Emit(Recorder.New(WrittenEvent, "cr-cli").With("path", rel).With("line", line.Length > 300 ? line.Substring(0, 300) : line)
                .With("after", WorkSnapshot.HashFile(full)));
            string dir = mirror ? CrLog.MirrorDir(root) : null;
            if (dir == null)
                return;
            Directory.CreateDirectory(dir);
            string m = Path.Combine(dir, target + ".txt");
            if (!File.Exists(m) || !File.ReadAllLines(m).Contains(line))
                File.AppendAllText(m, line + "\n");
        }

        /// <summary>
        /// A task starting on a CR filed in another worktree copies its lines into this checkout's tracked log, so the branch that
        /// carries the tests ([Covers("CR-n")]) also carries the CR they cover (ARCH111 on CI sees it without the mirror).
        /// </summary>
        public static int Adopt(string root, CrEntry cr)
        {
            var tracked = new HashSet<string>(CrLog.TrackedLines(root, cr.To), StringComparer.Ordinal);
            int n = 0;
            foreach (var line in cr.History.Where(l => !tracked.Contains(l)))
            {
                Append(root, cr.To, line, mirror: false);
                n++;
            }
            return n;
        }

        /// <summary>Is this content of a CR log one the gate wrote? (Bash running `task need` / `cr …` changes it; that change is allowed.)</summary>
        public static bool IsGateWrite(string root, string rel, string state) =>
            rel != null && rel.StartsWith(CrLog.Dir + "/", StringComparison.Ordinal) &&
            EventLog.For(root).ReadAll().Any(e => e.Type == WrittenEvent && e.Get("path") == rel && e.Get("after") == state);

        /// <summary>
        /// Next CR number, unique across worktrees: a counter in the git common dir (next to the task registry), never below
        /// what this checkout already holds (a CR merged from another branch).
        /// </summary>
        public static string NextId(string root)
        {
            string p = Path.Combine(Path.GetDirectoryName(TaskRegistry.PathOf(root)), "cr-counter.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            int local = Load(root).Keys.Select(k => int.TryParse(k.Substring(3), out int n) ? n : 0).DefaultIfEmpty(0).Max();
            for (int i = 0; ; i++)
            {
                try
                {
                    using (new FileStream(p + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                        int last = File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out int c) ? c : 0;
                        int next = Math.Max(last, local) + 1;
                        File.WriteAllText(p, next.ToString());
                        return "CR-" + next;
                    }
                }
                catch (IOException) when (i < 200)
                {
                    Thread.Sleep(10);
                }
            }
        }

        /// <summary>Are the CR's PublicApi lines already in the module's baseline (its contract part is done)?</summary>
        public static bool ApiInPlace(string root, CrEntry cr)
        {
            string p = Path.Combine(root, ContractsDir(cr.Module) + "PublicApi.txt");
            var have = new HashSet<string>((File.Exists(p) ? File.ReadAllLines(p) : new string[0]).Select(l => l.Trim()), StringComparer.Ordinal);
            return cr.Api.Select(l => l.Trim()).Where(l => !l.StartsWith("- ", StringComparison.Ordinal)).All(have.Contains)
                   && cr.Api.Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).All(l => !have.Contains(l.Substring(2).Trim()));
        }

        /// <summary>
        /// Contracts grants a task gets at start: every approved API CR it works on (B's side), and every approved API CR its own
        /// feature filed whose lines are not in place yet (A's side, e.g. a breaking CR the arbiter approved after A's task ended).
        /// </summary>
        public static Dictionary<string, string> GrantsFor(string root, IEnumerable<string> reqs, string fromFeature)
        {
            var grants = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(reqs, StringComparer.Ordinal);
            foreach (var cr in Load(root).Values.Where(c => c.IsRequirement && c.Api.Count > 0 && c.Module != "Core"))
                if (ids.Contains(cr.Id) || (fromFeature != null && cr.From == fromFeature && !ApiInPlace(root, cr)))
                    grants[cr.Id] = cr.Module;
            return grants;
        }

        /// <summary>A CR approved later (arbiter / owner): the asker's task, if still open in any worktree, gets its grant now.</summary>
        public static string GrantToAsker(string root, CrEntry cr)
        {
            if (cr.Api.Count == 0 || cr.Module == "Core" || cr.Task == null)
                return null;
            var entry = TaskRegistry.Load(root).FirstOrDefault(t => t.Id == cr.Task);
            if (entry == null)
                return null;
            var task = TaskStore.Load(entry.Worktree);
            if (task == null || !task.IsActive || task.Id != cr.Task)
                return null;
            task.CrGrants[cr.Id] = cr.Module;
            TaskStore.Save(entry.Worktree, task);
            return task.Id;
        }

        /// <summary>Contracts folder a CR may change ("Core" = Core/Runtime).</summary>
        public static string ContractsDir(string module) =>
            module == "Core" ? ScopePolicy.Game + "Core/Runtime/" : ScopePolicy.Game + "Features/" + module + "/Contracts/";

        public static bool IsGranted(AgentTask task, string path, out string cr)
        {
            cr = null;
            if (task == null || !task.IsActive || task.CrGrants == null)
                return false;
            foreach (var g in task.CrGrants)
            {
                string dir = ContractsDir(g.Value);
                if (path.StartsWith(dir, StringComparison.Ordinal) && (path.EndsWith(".cs", StringComparison.Ordinal) || path == dir + "PublicApi.txt"))
                {
                    cr = g.Key;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// What a task wrote under a CR grant beyond what the CR says: PublicApi.txt must change by exactly the CR's lines, a .cs file
        /// only by a skeleton (declarations, default bodies: B's own sessions write the implementation).
        /// </summary>
        public static List<string> GrantFindings(string root, AgentTask task, string rel)
        {
            if (!IsGranted(task, rel, out var id))
                return new List<string>();
            var (code, before) = AgentGit.Run(root, "show HEAD:" + rel);
            string full = Path.Combine(root, rel);
            string after = File.Exists(full) ? File.ReadAllText(full) : "";
            if (code != 0)
                before = null;
            List<string> problems;
            if (rel.EndsWith("/PublicApi.txt", StringComparison.Ordinal))
            {
                Load(root).TryGetValue(id, out var cr);
                problems = CrClassifier.ApiDiffViolations(before, after, cr?.Api ?? new List<string>());
            }
            else
                problems = TestRoles.SkeletonViolations(before, after);
            return problems.Take(5).Select(p => "CR " + rel + ": " + p + " (" + id + " allows only its own PublicApi lines and a skeleton; the owner module writes the code)").ToList();
        }
    }
}
