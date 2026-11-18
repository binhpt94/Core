using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>One failure and who can fix it, with the command for THAT person.</summary>
    public sealed class Fix
    {
        public string Who { get; set; }          // agent | human
        public string Problem { get; set; }
        public string Hint { get; set; }
        public List<string> Files { get; set; } = new List<string>();

        public override string ToString() => Problem + (Hint != null ? "  → " + Hint : "");
    }

    /// <summary>
    /// Who caused a red gate? The agent may only be blocked by what it did; a human's edit (plan typo, misplaced file,
    /// gameplay tweak, removed assert) is reported to the HUMAN with human commands, and pre-commit/CI still block the merge.
    /// Authorship is evidence from the event log: a path's last write is the agent's only if a hook/observer recorded the agent
    /// writing it; everything else (UNOBSERVED, baseline, never seen) is the human's.
    /// </summary>
    public static class Attribution
    {
        public const string HumanFixesFile = "human-fixes.json";

        /// <summary>path → "agent" | "human" (last observed writer).</summary>
        public static Dictionary<string, string> Authors(IEnumerable<AgentEvent> events)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in events)
            {
                string p = e.Get("path");
                if (p == null)
                    continue;
                if (e.Type == EventTypes.FileModified)
                    map[p] = e.Session == "human" ? "human" : "agent";
                else if (e.Type == EventTypes.UnobservedChange)
                    map[p] = e.Get("likely") == "agent" ? "agent" : "human";
                else if (e.Type == EventTypes.FileBaseline)
                    map[p] = "human";
            }
            return map;
        }

        public static string AuthorOf(string path, IReadOnlyDictionary<string, string> authors) =>
            authors.TryGetValue(path, out var a) ? a : "human";

        private static readonly HashSet<string> AssetAndPlanRules = new HashSet<string>
            { "ARCH073", "ARCH080", "ARCH081", "ARCH082", "ARCH083", "ARCH084", "ARCH093", "ARCH110", "ARCH111", "ARCH120", "ARCH130", "ARCH131", "ARCH132" };

        /// <param name="changed">files that differ from the base (uncommitted + committed on the branch)</param>
        public static List<Fix> Classify(IEnumerable<RuleViolation> violations, IEnumerable<string> failedTests, string failedStage,
            IReadOnlyCollection<string> changed, IReadOnlyDictionary<string, string> authors)
        {
            var agentChanged = changed.Where(p => AuthorOf(p, authors) == "agent").ToList();
            var humanChanged = changed.Where(p => AuthorOf(p, authors) == "human").ToList();
            bool agentTouchedCode = agentChanged.Any(IsCode);
            // plan/inbox/ = proposals the agent is supposed to write; no rule reads them
            bool agentTouchedAssetsOrPlan = agentChanged.Where(p => !p.StartsWith("plan/inbox/", StringComparison.Ordinal)).Any(p => AssetRules.IsYaml(p) || p.StartsWith("plan/", StringComparison.Ordinal) || p.EndsWith(".meta", StringComparison.Ordinal) || !p.StartsWith("Assets/_Game/", StringComparison.Ordinal) && p.StartsWith("Assets/", StringComparison.Ordinal));
            var fixes = new List<Fix>();

            foreach (var v in violations)
            {
                string who;
                if (AuthorOf(v.Path, authors) == "agent" && changed.Contains(v.Path))
                    who = "agent";
                else if (changed.Contains(v.Path))
                    who = "human";
                else if (v.RuleId == "ARCH090")                     // generated docs: whoever changed what they are generated from
                    who = IsAssetDrift(v) ? (agentTouchedAssetsOrPlan ? "agent" : "human") : (agentTouchedCode ? "agent" : "human");
                else if (agentChanged.Count == 0)
                    who = "human";
                else if (AssetAndPlanRules.Contains(v.RuleId) && !agentTouchedAssetsOrPlan)
                    who = "human";
                else
                    who = "agent";
                fixes.Add(new Fix { Who = who, Problem = v.ToString(), Hint = who == "human" ? HumanHint(v) : null, Files = { v.Path } });
            }

            var tests = failedTests.ToList();
            if (tests.Count > 0 || failedStage == "build")
            {
                bool agentsFault = agentTouchedCode || humanChanged.Count(IsCode) == 0; // nobody else changed code: it can only be the agent
                string problem = failedStage == "build" ? "build error" : tests.Count + " failing test(s): " + string.Join(", ", tests.Take(5));
                var humanCode = humanChanged.Where(IsCode).ToList();
                fixes.Add(new Fix
                {
                    Who = agentsFault ? "agent" : "human",
                    Problem = problem,
                    Hint = agentsFault ? null : TestHint(tests, humanCode),
                    Files = agentsFault ? agentChanged.Where(IsCode).ToList() : humanCode
                });
            }
            return fixes;
        }

        /// <summary>The stale part of a generated doc is about assets (counts, "refs UNKNOWN"), not code structure.</summary>
        public static bool IsAssetDrift(RuleViolation v) =>
            v.Message != null && (v.Message.Contains("Assets (Public/Private)") || v.Message.Contains("Asset references"));

        /// <summary>Files the Unity editor writes (scenes, prefabs, assets, .meta): what MCP edits end up as.</summary>
        public static bool IsUnitySerialized(string p) => AssetRules.IsYaml(p) || p.EndsWith(".meta", StringComparison.Ordinal);

        public static bool IsCode(string p) =>
            p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".asmdef", StringComparison.Ordinal) || p.EndsWith(".csproj", StringComparison.Ordinal)
            || p.EndsWith(".replay.txt", StringComparison.Ordinal) || p.EndsWith(".layout.txt", StringComparison.Ordinal);

        /// <summary>Commands for a person (not for the agent): what to run if it was a slip, what to do if it was intended.</summary>
        public static string HumanHint(RuleViolation v)
        {
            switch (v.RuleId)
            {
                case "ARCH110":
                case "ARCH111":
                    return "sửa " + v.Path + (v.Line > 0 ? " dòng " + v.Line : "") + " theo docs/PLANNING.md; lỡ tay: git checkout -- " + v.Path;
                case "ARCH130":
                case "ARCH131":
                    return "dời TRONG UNITY vào Assets/_Game/Features/<F>/Assets/Private/ (đồ tạm: .scratch/), hoặc xóa; lỡ tay: git checkout/clean";
                case "ARCH132":
                    return "chạy đúng lệnh trong thông báo (khôi phục .meta), hoặc gỡ tham chiếu trong Unity";
                case "ARCH090":
                    return "./tools/test-gate.sh graph --write (docs được sinh tự động)";
                case "ARCH084":
                    return "mở project trong Unity để sinh .meta rồi commit cả .meta";
                default:
                    return "xem docs/RULES.md#" + v.RuleId.ToLowerInvariant() + "; lỡ tay: git checkout -- " + v.Path;
            }
        }

        public static string TestHint(IReadOnlyCollection<string> tests, IReadOnlyCollection<string> humanCode)
        {
            string undo = humanCode.Count > 0 ? "lỡ tay: git checkout -- " + string.Join(" ", humanCode.Take(4)) : "lỡ tay: hoàn tác thay đổi của bạn";
            bool golden = tests.Any(t => t.IndexOf("Golden", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0);
            return undo + "; cố ý " + (golden
                ? "đổi gameplay: tăng SimVersion (QuizBattle.cs), GOLDEN_WRITE=1 dotnet test LogicTests, xem diff Tests/Replays/; nếu ý đồ đổi theo, sửa requirement trong plan/features.md"
                : "đổi hành vi: cập nhật test tương ứng rồi commit");
        }

        public static string TestGuardHint(string path) => "lỡ tay: git checkout -- " + path + "; cố ý bỏ: commit để thay đổi đó thành mốc mới";

        // ------------------------------------------------------------------ persistence (dashboard, SessionStart, the human's terminal)

        public static string PathOf(string root) => Path.Combine(root, ".agent", HumanFixesFile);

        public static void Save(string root, List<Fix> humanFixes)
        {
            string p = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            if (humanFixes.Count == 0)
            {
                if (File.Exists(p))
                    File.Delete(p);
                return;
            }
            File.WriteAllText(p, JsonSerializer.Serialize(humanFixes, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static List<Fix> Load(string root)
        {
            string p = PathOf(root);
            try { return File.Exists(p) ? JsonSerializer.Deserialize<List<Fix>>(File.ReadAllText(p)) ?? new List<Fix>() : new List<Fix>(); }
            catch (JsonException) { return new List<Fix>(); }
        }
    }
}
