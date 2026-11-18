using System;
using System.Collections.Generic;
using System.Linq;

namespace TestGate.AgentControl
{
    /// <summary>Trust level of a node: where its truth comes from. Never set from what an agent says.</summary>
    public static class Trust
    {
        public const string Planned = "planned";       // proposed/requested, not done (e.g. task waiting for approval)
        public const string Observed = "observed";     // a trusted observer saw it happen
        public const string Verified = "verified";     // a check passed AFTER it (gate/tests with result-file hash)
        public const string Pending = "pending";       // check not run (e.g. Unity not available)
        public const string Failed = "failed";         // a check ran and failed
        public const string Denied = "denied";         // blocked before it happened
        public const string Violation = "violation";   // happened, broke a policy
        public const string Unobserved = "unobserved"; // on disk, but no trusted observer saw it (human/IDE/other tool?)
        public const string Claim = "claim";           // the agent SAYS so: not evidence
        public const string Info = "info";
    }

    public sealed class DashboardModel
    {
        public string Generated { get; set; }
        public ChainStatus Chain { get; set; }
        public TaskView Task { get; set; }
        public List<LaneView> Lanes { get; set; } = new List<LaneView>();
        public List<StepView> Steps { get; set; } = new List<StepView>();
        public List<FileView> Files { get; set; } = new List<FileView>();
        public List<GateView> Gates { get; set; } = new List<GateView>();
        public List<ClaimView> Claims { get; set; } = new List<ClaimView>();
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        /// <summary>Product roadmap (computed by TestGate.Planning; null without Docs/Plan/features.md).</summary>
        public object Roadmap { get; set; }
        /// <summary>Housekeeping: loose/orphan/junk files, unused assets, disk use, layout/GUID violations (TestGate.Files).</summary>
        public object Housekeeping { get; set; }
        /// <summary>Failures caused by a human's edits, with the commands for the human (from the last Stop).</summary>
        public List<Fix> HumanFixes { get; set; } = new List<Fix>();
    }

    public sealed class TaskView
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string Scope { get; set; }
        public string Requested { get; set; }
        public string Reason { get; set; }
    }

    public sealed class LaneView
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Kind { get; set; }          // main | subagent | gate
        public string Parent { get; set; }
        public long SpawnSeq { get; set; }
        public string Started { get; set; }
        public string Stopped { get; set; }
        public int Steps { get; set; }
    }

    public sealed class StepView
    {
        public long Seq { get; set; }
        public List<long> Seqs { get; set; } = new List<long>();
        public string Ts { get; set; }
        public string Lane { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Trust { get; set; }
        public string Source { get; set; }
        public string Hash { get; set; }
    }

    public sealed class FileView
    {
        public string Path { get; set; }
        public string Trust { get; set; }
        public string Via { get; set; }
        public string Scope { get; set; }
        public int Changes { get; set; }
        public long LastSeq { get; set; }
    }

    public sealed class GateView
    {
        public long Seq { get; set; }
        public string Ts { get; set; }
        public string Lane { get; set; }
        public string Result { get; set; }
        public string Plan { get; set; }
        public List<string> Tests { get; set; } = new List<string>();
        public string Trust { get; set; }
    }

    public sealed class ClaimView
    {
        public long Seq { get; set; }
        public string Lane { get; set; }
        public string Text { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
        public string Verdict { get; set; }
    }

    /// <summary>
    /// events → model. Pure and deterministic: the same event log always gives the same graph, so the graph can be
    /// thrown away and rebuilt at any time ("graph = projection of the log", never a picture an AI drew).
    /// </summary>
    public static class Reducer
    {
        public static readonly HashSet<string> Browsing = new HashSet<string> { "Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch", "NotebookRead", "TodoWrite", "ToolSearch" };

        public static DashboardModel Build(IReadOnlyList<AgentEvent> events, ChainStatus chain, AgentTask task, string generated)
        {
            var m = new DashboardModel { Generated = generated, Chain = chain };
            if (task != null)
                m.Task = new TaskView
                {
                    Id = task.Id, Title = task.Title, Status = task.Status, Scope = task.ScopeText,
                    Requested = task.HasPendingRequest ? string.Join(", ", task.RequestedFeatures.Select(f => "Features/" + f).Concat(task.RequestedPaths)) + (task.RequestedTestChanges ? " +test changes" : "") : null,
                    Reason = task.RequestReason
                };

            var lanes = new Dictionary<string, LaneView>(StringComparer.Ordinal);
            LaneView Lane(AgentEvent e)
            {
                string id = LaneId(e);
                if (!lanes.TryGetValue(id, out var l))
                {
                    bool gate = e.Session == null || e.Session == "cli";
                    l = new LaneView
                    {
                        Id = id, Started = e.Ts, Kind = gate ? "gate" : e.Agent == null || e.Agent == "main" ? "main" : "subagent",
                        Label = gate ? "CLI: task / gate (không rõ phiên)" : (e.Agent == null || e.Agent == "main" ? "Claude " : e.Agent + " ") + Short(e.Session)
                    };
                    if (l.Kind == "subagent")
                        l.Parent = (e.Session ?? "") + "/main";
                    lanes[id] = l;
                }
                return l;
            }

            StepView last = null;
            var fileMap = new Dictionary<string, FileView>(StringComparer.Ordinal);
            var claimsSince = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var lastGateByLane = new Dictionary<string, GateView>(StringComparer.Ordinal);
            GateView lastGate = null;
            var testsSinceGate = new List<string>();
            var blockedSinceClaim = new Dictionary<string, string>(StringComparer.Ordinal);
            var violationsSinceClaim = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var e in events)
            {
                var lane = Lane(e);
                string trust = TrustOf(e);
                string title = TitleOf(e);

                // Collapse runs of browsing (Read/Grep/...) in the same lane into one node.
                bool browse = e.Type == EventTypes.ToolUsed && Browsing.Contains(e.Get("tool") ?? "");
                if (browse && last != null && last.Lane == lane.Id && last.Kind == "browse")
                {
                    last.Seqs.Add(e.Seq);
                    last.Title = "Đọc / tìm × " + last.Seqs.Count;
                    last.Detail = last.Detail.Split('\n').Length < 25 ? last.Detail + "\n" + Describe(e) : last.Detail;
                    continue;
                }

                var step = new StepView
                {
                    Seq = e.Seq, Seqs = { e.Seq }, Ts = e.Ts, Lane = lane.Id, Kind = browse ? "browse" : KindOf(e),
                    Title = browse ? "Đọc / tìm × 1" : title, Detail = Describe(e), Trust = trust, Source = e.Source, Hash = e.Hash
                };
                m.Steps.Add(step);
                last = step;
                lane.Steps++;

                if (trust == Trust.Violation || trust == Trust.Unobserved)
                    violationsSinceClaim[lane.Id] = (violationsSinceClaim.TryGetValue(lane.Id, out var vc) ? vc : 0) + 1;

                switch (e.Type)
                {
                    case EventTypes.TestResult:
                        testsSinceGate.Add(e.Get("suite") + ": " + e.Get("result") +
                                           (e.Get("total") != null && e.Get("total") != "0" ? " (" + e.Get("passed") + "/" + e.Get("total") + ")" : "") +
                                           (e.Get("resultsHash") != null ? " · kết quả #" + e.Get("resultsHash") : "") +
                                           (e.Get("result") != "passed" && e.Get("note") != null ? " · " + e.Get("note") : ""));
                        break;
                    case EventTypes.StopBlocked:
                        blockedSinceClaim[lane.Id] = e.Get("reason");
                        break;
                    case EventTypes.AgentSpawned:
                        string child = (e.Session ?? "") + "/" + (e.Get("agent") ?? "subagent");
                        if (!lanes.ContainsKey(child))
                            lanes[child] = new LaneView { Id = child, Kind = "subagent", Label = (e.Get("agent") ?? "subagent") + " " + Short(e.Session), Parent = lane.Id, SpawnSeq = e.Seq, Started = e.Ts };
                        break;
                    case EventTypes.AgentStopped:
                        lane.Stopped = e.Ts;
                        break;
                    case EventTypes.FileModified:
                    case EventTypes.UnobservedChange:
                    case EventTypes.FileBaseline:
                        string path = e.Get("path");
                        if (path == null)
                            break;
                        if (!fileMap.TryGetValue(path, out var f))
                            fileMap[path] = f = new FileView { Path = path, Trust = Trust.Observed };
                        f.LastSeq = e.Seq;
                        f.Via = e.Get("via") ?? e.Type.ToLowerInvariant();
                        if (e.Type != EventTypes.FileBaseline)
                            f.Changes++;
                        f.Scope = e.Get("scope") ?? f.Scope;
                        if (e.Type == EventTypes.FileBaseline)
                            f.Trust = f.Trust == Trust.Observed ? Trust.Info : f.Trust;
                        else if (trust == Trust.Violation || f.Trust == Trust.Violation)
                            f.Trust = Trust.Violation;
                        else if (trust == Trust.Unobserved)
                            f.Trust = Trust.Unobserved;
                        else
                            f.Trust = Trust.Observed;
                        if (!claimsSince.TryGetValue(lane.Id, out var list))
                            claimsSince[lane.Id] = list = new List<string>();
                        if (e.Type == EventTypes.FileModified)
                            list.Add(path);
                        break;
                    case EventTypes.GateFinished:
                        var g = new GateView { Seq = e.Seq, Ts = e.Ts, Lane = lane.Id, Result = e.Get("result"), Plan = e.Get("plan"), Trust = trust };
                        g.Tests.AddRange(testsSinceGate);
                        testsSinceGate.Clear();
                        m.Gates.Add(g);
                        lastGate = g;
                        lastGateByLane[lane.Id] = g;
                        // Files changed before a PASSING gate are verified (until they change again).
                        if (g.Result == "passed")
                            foreach (var file in fileMap.Values.Where(x => x.Trust == Trust.Observed && x.LastSeq < e.Seq))
                                file.Trust = Trust.Verified;
                        else if (g.Result == "pending")
                            foreach (var file in fileMap.Values.Where(x => x.Trust == Trust.Observed && x.LastSeq < e.Seq))
                                file.Trust = Trust.Pending;
                        break;
                    case EventTypes.Claim:
                        var c = new ClaimView { Seq = e.Seq, Lane = lane.Id, Text = e.Get("text") };
                        var changed = claimsSince.TryGetValue(lane.Id, out var cl) ? cl.Distinct().ToList() : new List<string>();
                        c.Evidence.Add(changed.Count == 0 ? "không có file nào được quan sát là đã sửa" : changed.Count + " file đã sửa (quan sát được): " + string.Join(", ", changed.Take(6)) + (changed.Count > 6 ? "…" : ""));
                        var gate = lastGate;
                        c.Evidence.Add(gate == null ? "gate: CHƯA CHẠY" : "gate #" + gate.Seq + ": " + gate.Result + (gate.Tests.Count > 0 ? " (" + string.Join("; ", gate.Tests) + ")" : ""));
                        int violations = violationsSinceClaim.TryGetValue(lane.Id, out var vn) ? vn : 0;
                        if (violations > 0)
                            c.Evidence.Add(violations + " vi phạm / thay đổi không quan sát được kể từ lời khai trước");
                        bool blocked = blockedSinceClaim.TryGetValue(lane.Id, out var why);
                        if (blocked)
                            c.Evidence.Add("Stop bị CHẶN: " + why);
                        c.Verdict = blocked || violations > 0 || gate?.Result == "failed" ? "mâu thuẫn với bằng chứng"
                            : gate == null ? "không có bằng chứng"
                            : gate.Result == "passed" ? "có bằng chứng" : "một phần (Unity pending)";
                        m.Claims.Add(c);
                        claimsSince[lane.Id] = new List<string>();
                        violationsSinceClaim[lane.Id] = 0;
                        blockedSinceClaim.Remove(lane.Id);
                        break;
                }
            }

            m.Lanes = lanes.Values.OrderBy(l => l.Kind == "gate" ? 2 : 0).ThenBy(l => l.Started, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
            m.Files = fileMap.Values.Where(f => f.Changes > 0 || f.Trust != Trust.Info).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            m.Counts["events"] = events.Count;
            m.Counts["denied"] = events.Count(e => e.Type == EventTypes.PolicyDenied);
            m.Counts["violations"] = events.Count(e => TrustOf(e) == Trust.Violation);
            m.Counts["unobserved"] = events.Count(e => e.Type == EventTypes.UnobservedChange);
            m.Counts["gatesPassed"] = m.Gates.Count(g => g.Result == "passed");
            m.Counts["gatesFailed"] = m.Gates.Count(g => g.Result == "failed");
            m.Counts["gatesPending"] = m.Gates.Count(g => g.Result == "pending");
            m.Counts["stopBlocked"] = events.Count(e => e.Type == EventTypes.StopBlocked);
            m.Counts["claims"] = m.Claims.Count;
            m.Counts["filesChanged"] = m.Files.Count(f => f.Changes > 0);
            return m;
        }

        public static string LaneId(AgentEvent e) => (e.Session ?? "cli") + "/" + (e.Agent ?? "main");

        private static string Short(string session) => string.IsNullOrEmpty(session) ? "" : "·" + (session.Length > 6 ? session.Substring(0, 6) : session);

        public static string TrustOf(AgentEvent e)
        {
            switch (e.Type)
            {
                case EventTypes.PolicyDenied: return Trust.Denied;
                case EventTypes.PolicyViolation: return Trust.Violation;
                case EventTypes.UnobservedChange: return Trust.Unobserved;
                case EventTypes.Claim: return Trust.Claim;
                case EventTypes.StopBlocked: return Trust.Failed;
                case EventTypes.FileModified: return e.Get("inScope") == "false" ? Trust.Violation : Trust.Observed;
                case EventTypes.TestResult: return e.Get("result") == "passed" ? Trust.Verified : e.Get("result") == "failed" ? Trust.Failed : Trust.Pending;
                case EventTypes.GateFinished: return e.Get("result") == "passed" ? Trust.Verified : e.Get("result") == "pending" ? Trust.Pending : Trust.Failed;
                case EventTypes.TaskCreated:
                case EventTypes.TaskScopeRequested: return e.Get("status") == AgentTask.PendingApproval || e.Type == EventTypes.TaskScopeRequested ? Trust.Planned : Trust.Observed;
                case EventTypes.FileBaseline: return Trust.Info;
                default: return Trust.Observed;
            }
        }

        private static string KindOf(AgentEvent e)
        {
            switch (e.Type)
            {
                case EventTypes.FileModified: case EventTypes.UnobservedChange: case EventTypes.FileBaseline: return "file";
                case EventTypes.TestResult: return "test";
                case EventTypes.GateStarted: case EventTypes.GateFinished: return "gate";
                case EventTypes.PolicyDenied: case EventTypes.PolicyViolation: return "policy";
                case EventTypes.Claim: return "claim";
                case EventTypes.AgentSpawned: case EventTypes.AgentStopped: case EventTypes.SessionStarted: return "agent";
                case EventTypes.CommandRun: return "command";
                case EventTypes.StopAllowed: case EventTypes.StopBlocked: return "stop";
                default: return e.Type.StartsWith("TASK_", StringComparison.Ordinal) ? "task" : "tool";
            }
        }

        public static string TitleOf(AgentEvent e)
        {
            switch (e.Type)
            {
                case EventTypes.SessionStarted: return "Phiên bắt đầu (" + (e.Get("source") ?? "startup") + ")";
                case EventTypes.TaskCreated: return "Task " + e.Get("id") + (e.Get("status") == AgentTask.PendingApproval ? " (chờ duyệt)" : "");
                case EventTypes.TaskScopeRequested: return "Xin mở rộng phạm vi";
                case EventTypes.TaskApproved: return "Người duyệt task " + e.Get("id");
                case EventTypes.TaskClosed: return "Đóng task " + e.Get("id");
                case EventTypes.AgentSpawned: return "Tạo agent " + (e.Get("agent") ?? "");
                case EventTypes.AgentStopped: return "Agent kết thúc";
                case EventTypes.ToolUsed: return e.Get("tool");
                case EventTypes.CommandRun: return "Bash: " + Trim(e.Get("command"), 48);
                case EventTypes.FileModified: return (e.Get("inScope") == "false" ? "NGOÀI PHẠM VI: " : "Sửa ") + FileName(e.Get("path"));
                case EventTypes.FileBaseline: return "Đã bẩn sẵn: " + FileName(e.Get("path"));
                case EventTypes.UnobservedChange: return "Không quan sát được: " + FileName(e.Get("path"));
                case EventTypes.PolicyDenied: return "CHẶN: " + Trim(e.Get("target"), 40);
                case EventTypes.PolicyViolation: return "VI PHẠM: " + Trim(e.Get("rule"), 40);
                case EventTypes.GateStarted: return "Gate bắt đầu";
                case EventTypes.TestResult: return e.Get("suite") + ": " + e.Get("result");
                case EventTypes.GateFinished: return "Gate: " + e.Get("result");
                case EventTypes.StopBlocked: return "Không cho kết thúc (gate đỏ)";
                case EventTypes.StopAllowed: return "Cho phép kết thúc";
                case EventTypes.Claim: return "AI nói: " + Trim(e.Get("text"), 40);
                default: return e.Type;
            }
        }

        public static string Describe(AgentEvent e) =>
            "#" + e.Seq + " " + e.Type + " · " + e.Source + " · " + e.Ts + "\n" +
            string.Join("\n", e.Data.Select(kv => kv.Key + ": " + Trim(kv.Value, 300)));

        private static string FileName(string path) => path == null ? "" : path.Substring(path.LastIndexOf('/') + 1);

        private static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
