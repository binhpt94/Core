using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    public sealed class RegisteredTask
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Worktree { get; set; }
        public List<string> Modules { get; set; } = new List<string>();
        public List<string> Requirements { get; set; } = new List<string>();
        public string Role { get; set; }           // TestRoles: tests and impl of one module may run side by side
        public string Status { get; set; }
        public string StartedUtc { get; set; }
    }

    /// <summary>
    /// Open tasks of ALL worktrees of this repository (parallel agents = one git worktree each). Lives in the git common dir,
    /// so every worktree sees the same list: two tasks may run in parallel only if their code modules don't overlap
    /// (resource conflicts are derived from scope, never declared by hand).
    /// </summary>
    public static class TaskRegistry
    {
        public static string PathOf(string root)
        {
            var (code, output) = AgentGit.Run(root, "rev-parse --git-common-dir");
            string common = code == 0 && output.Trim().Length > 0 ? output.Trim() : Path.Combine(root, ".git");
            if (!Path.IsPathRooted(common))
                common = Path.GetFullPath(Path.Combine(root, common));
            return Path.Combine(common, "agent-control", "tasks.json");
        }

        public static List<RegisteredTask> Load(string root) => Raw(root).Where(IsLive).ToList();

        private static List<RegisteredTask> Raw(string root)
        {
            string p = PathOf(root);
            return File.Exists(p) ? JsonSerializer.Deserialize<List<RegisteredTask>>(File.ReadAllText(p)) ?? new List<RegisteredTask>() : new List<RegisteredTask>();
        }

        /// <summary>
        /// A lock is real only while its worktree exists AND that worktree's own task file still says this task is open. A crashed
        /// agent keeps its lock (its task is still open there: the next agent in that worktree continues or closes it); a removed
        /// worktree or a task closed/replaced by hand releases it. Verified from files, never from timeouts.
        /// </summary>
        public static bool IsLive(RegisteredTask t)
        {
            if (t.Status == AgentTask.Closed || string.IsNullOrEmpty(t.Worktree) || !Directory.Exists(t.Worktree))
                return false;
            try
            {
                var there = TaskStore.Load(t.Worktree);
                return there != null && there.Id == t.Id && there.Status != AgentTask.Closed;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Other worktrees' open tasks whose modules overlap these AND that write the same folders (TestRoles.Conflict).</summary>
        public static List<RegisteredTask> Conflicts(string root, IEnumerable<string> modules, string role = null)
        {
            var mine = new HashSet<string>(modules, StringComparer.Ordinal);
            string here = Path.GetFullPath(root);
            return Load(root).Where(t => t.Status != AgentTask.Closed && Path.GetFullPath(t.Worktree) != here && t.Modules.Any(mine.Contains)
                                         && TestRoles.Conflict(t.Role, role)).ToList();
        }

        public static void Upsert(string root, AgentTask task, IEnumerable<string> requirements)
        {
            Update(root, list =>
            {
                list.RemoveAll(t => t.Id == task.Id || (Path.GetFullPath(t.Worktree) == Path.GetFullPath(root) && t.Status == AgentTask.Closed));
                if (task.Status != AgentTask.Closed)
                    list.Add(new RegisteredTask
                    {
                        Id = task.Id, Title = task.Title, Worktree = Path.GetFullPath(root), Modules = task.Features.ToList(),
                        Requirements = requirements.ToList(), Role = task.Role, Status = task.Status, StartedUtc = task.CreatedTs
                    });
            });
        }

        public static void Remove(string root, string id) => Update(root, list => list.RemoveAll(t => t.Id == id));

        private static void Update(string root, Action<List<RegisteredTask>> change)
        {
            string p = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            for (int i = 0; ; i++)
            {
                try
                {
                    using (new FileStream(p + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                        var list = Raw(root).Where(IsLive).ToList(); // stale locks are dropped on every write
                        change(list);
                        File.WriteAllText(p, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                        return;
                    }
                }
                catch (IOException) when (i < 200)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }
}
