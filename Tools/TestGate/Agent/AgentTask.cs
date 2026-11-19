using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TestGate.AgentControl
{
    /// <summary>
    /// The unit of permission. Scope is derived from the architecture (feature folders), not from a hand-written list
    /// of files: one feature = one task the agent may open itself. Anything wider (more features, Core, Bootstrap,
    /// explicit paths, changing tests' expectations) waits for a human `task approve` (needs a terminal; denied to agents).
    /// </summary>
    public sealed class AgentTask
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }                    // active | pending_approval | closed
        public List<string> Features { get; set; } = new List<string>();
        public List<string> Paths { get; set; } = new List<string>();
        public bool AllowTestChanges { get; set; }
        public List<string> RequestedFeatures { get; set; } = new List<string>();
        public List<string> RequestedPaths { get; set; } = new List<string>();
        public bool RequestedTestChanges { get; set; }
        /// <summary>Core maintenance (human-approved): owner-only paths become writable, except the gate's own log folder.</summary>
        public bool CoreMaintenance { get; set; }
        public bool RequestedCoreMaintenance { get; set; }
        public string RequestReason { get; set; }
        public string CreatedTs { get; set; }
        public string ApprovedTs { get; set; }
        /// <summary>Plan requirements this task works on (id → requirement hash at start: a change makes the task STALE).</summary>
        public Dictionary<string, string> Requirements { get; set; } = new Dictionary<string, string>();
        /// <summary>Started under rush: integration debt the plan keeps tracking.</summary>
        public List<string> RushDebt { get; set; } = new List<string>();

        /// <summary>Files already dirty when the task started (path → hash): not the task's changes.</summary>
        public Dictionary<string, string> Baseline { get; set; } = new Dictionary<string, string>();
        /// <summary>tests | impl | arbiter | null (TestRoles): who writes the tests vs the code of its requirements.</summary>
        public string Role { get; set; }
        /// <summary>CR id → module: an approved CR lets this task write that module's Contracts (its PublicApi lines + a skeleton, nothing else).</summary>
        public Dictionary<string, string> CrGrants { get; set; } = new Dictionary<string, string>();

        /// <summary>Maintenance tasks: [Covers] test results of its features at start (Provenance: red → green later = untraced behaviour).</summary>
        public Dictionary<string, string> CoverBaseline { get; set; }

        /// <summary>Where the work comes from: plan requirements / approved CRs, core maintenance, or none (maintenance: no behaviour change).</summary>
        public string Provenance => CoreMaintenance ? "core" : Requirements.Count > 0 ? "plan" : "maintenance";

        public const string Active = "active";
        public const string PendingApproval = "pending_approval";
        public const string Closed = "closed";

        public bool IsActive => Status == Active;
        public bool HasPendingRequest => RequestedFeatures.Count > 0 || RequestedPaths.Count > 0 || RequestedTestChanges || RequestedCoreMaintenance;

        public string ScopeText =>
            string.Join(", ", Features.Select(f => "Features/" + f).Concat(Paths)) + (AllowTestChanges ? " (+test changes)" : "")
            + (CoreMaintenance ? " (+core maintenance)" : "");
    }

    public static class TaskStore
    {
        public static string PathOf(string root) => Path.Combine(root, ".agent", "task.json");

        public static AgentTask Load(string root)
        {
            string p = PathOf(root);
            return File.Exists(p) ? JsonSerializer.Deserialize<AgentTask>(File.ReadAllText(p)) : null;
        }

        public static void Save(string root, AgentTask task)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(root)));
            File.WriteAllText(PathOf(root), JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public sealed class ScopeDecision
    {
        public bool Allowed { get; }
        public string Reason { get; }

        private ScopeDecision(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        public static ScopeDecision Allow(string reason) => new ScopeDecision(true, reason);
        public static ScopeDecision Deny(string reason) => new ScopeDecision(false, reason);
    }

    public static class ScopePolicy
    {
        public const string Game = "UnityProject/Assets/_Game/";

        /// <summary>Owner-only paths: never writable by an agent, whatever the task says (mirrors .claude/settings.json + CODEOWNERS).</summary>
        public static readonly string[] Protected =
        {
            ".agent/", ".claude/", ".github/", "Tools/", "Docs/Plan/features.md", "Docs/Plan/Core.md", "Docs/Plan/accepted.txt", "Docs/Plan/Seals/", "Docs/Plan/CRs/", "Docs/Plan/art-accepted.txt", "UnityProject/ProjectSettings/", "Tools/LogicTests/LogicTests.csproj", "Tools/ArchitecturePolicy.txt", "CLAUDE.md",
            Game + "Architecture/", Game + "Tests/Architecture/", Game + "Core/Editor/", "UnityProject/Assets/ThirdParty/", "UnityProject/Assets/Plugins/", "Docs/RULES.md"
        };

        /// <summary>The gate's own event log + task: never writable by an agent, not even in core maintenance.</summary>
        public const string GateLog = "." + "agent/";

        private static readonly string[] ProtectedSegments = { "/Tests/Replays/", "/Tests/Layout/" };

        /// <summary>The owner's notes (Dashboard): an AI edits them on request, in any task. Core.md goes through PlanEditGate (yes/no).</summary>
        public static bool IsDashboardSource(string path) => path == ProjectNotes.RelativePath;

        /// <summary>Written only by generators whose output a rule verifies (ARCH090): safe for any active task.</summary>
        public static bool IsGenerated(string path) => path.StartsWith("Docs/Architecture/", StringComparison.Ordinal);

        public static bool IsProtected(string path) =>
            Protected.Any(p => path == p.TrimEnd('/') || path.StartsWith(p, StringComparison.Ordinal))
            || ProtectedSegments.Any(s => path.Contains(s))
            || path.EndsWith("/PublicApi.txt", StringComparison.Ordinal) || path.EndsWith(".meta", StringComparison.Ordinal);

        /// <param name="path">repo-relative, '/' separated; null/outside the repo = not our business.</param>
        public static ScopeDecision Check(AgentTask task, string path)
        {
            if (path == null || path.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(path))
                return ScopeDecision.Allow("outside the repository");
            if (path.StartsWith(".scratch/", StringComparison.Ordinal))
                return ScopeDecision.Allow("scratch (gitignored, outside Assets/)");
            if (task != null && task.IsActive && task.CoreMaintenance && !path.StartsWith(GateLog, StringComparison.Ordinal))
                return ScopeDecision.Allow("core maintenance (approved by a human)");
            if (CrStore.IsGranted(task, path, out var cr))
                return ScopeDecision.Allow("contracts change approved by " + cr + " (checked: its PublicApi lines + a skeleton only)");
            if (IsProtected(path))
                return ScopeDecision.Deny(path + " is owner-only (protected); ask the human");
            if (task == null || task.Status == AgentTask.Closed)
                return ScopeDecision.Deny("no active task. Start one first: ./Tools/test-gate.sh task start \"<what>\" --feature <Feature>");
            if (task.Status == AgentTask.PendingApproval)
                return ScopeDecision.Deny("task " + task.Id + " waits for human approval (scope: " + task.ScopeText + ")");
            if (IsGenerated(path))
                return ScopeDecision.Allow("generated output (verified by ARCH090)");
            if (path.StartsWith("Docs/Plan/Inbox/", StringComparison.Ordinal))
                return ScopeDecision.Allow("proposal for the human (Docs/Plan/Inbox)");
            if (IsDashboardSource(path))
                return ScopeDecision.Allow("dashboard source the owner asked an AI to fill");
            foreach (var f in task.Features)
                if (path.StartsWith(Game + "Features/" + f + "/", StringComparison.Ordinal))
                    return TestRoles.Check(task.Role, Game + "Features/" + f + "/", f, path);
            foreach (var p in task.Paths)
                if (path == p || path.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal))
                    return ScopeDecision.Allow("approved path " + p);
            return ScopeDecision.Deny(path + " is outside task " + task.Id + " (scope: " + task.ScopeText + "). Wider scope: ./Tools/test-gate.sh task request --feature <F> | --path <P> \"<why>\" (a human approves)");
        }

        /// <summary>A new task: one feature → active at once; anything wider → pending_approval.</summary>
        public static AgentTask Start(string id, string title, IEnumerable<string> features, IEnumerable<string> paths, bool testChanges, string ts)
        {
            var task = new AgentTask
            {
                Id = id,
                Title = title,
                CreatedTs = ts,
                Features = features.Distinct().ToList(),
                Paths = paths.Select(p => p.Replace('\\', '/').TrimStart('.', '/')).Distinct().ToList(),
                AllowTestChanges = false,
                RequestedTestChanges = testChanges
            };
            bool selfService = task.Features.Count == 1 && task.Paths.Count == 0 && !testChanges;
            task.Status = selfService ? AgentTask.Active : AgentTask.PendingApproval;
            return task;
        }

        /// <summary>Asks for more scope; the task keeps its current scope until a human approves.</summary>
        public static void Request(AgentTask task, IEnumerable<string> features, IEnumerable<string> paths, bool testChanges, string reason, bool coreMaintenance = false)
        {
            task.RequestedFeatures = task.RequestedFeatures.Union(features).Where(f => !task.Features.Contains(f)).ToList();
            task.RequestedPaths = task.RequestedPaths.Union(paths.Select(p => p.Replace('\\', '/').TrimStart('.', '/'))).Where(p => !task.Paths.Contains(p)).ToList();
            task.RequestedTestChanges |= testChanges;
            task.RequestedCoreMaintenance |= coreMaintenance;
            task.RequestReason = reason;
        }

        public static void Approve(AgentTask task, string ts)
        {
            task.Features = task.Features.Union(task.RequestedFeatures).ToList();
            task.Paths = task.Paths.Union(task.RequestedPaths).ToList();
            task.AllowTestChanges |= task.RequestedTestChanges;
            task.CoreMaintenance |= task.RequestedCoreMaintenance;
            task.RequestedFeatures.Clear();
            task.RequestedPaths.Clear();
            task.RequestedTestChanges = false;
            task.RequestedCoreMaintenance = false;
            task.Status = AgentTask.Active;
            task.ApprovedTs = ts;
        }
    }
}
