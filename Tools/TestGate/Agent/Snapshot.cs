using System;
using System.Collections.Generic;
using System.Linq;

namespace TestGate.AgentControl
{
    /// <summary>
    /// What the dashboard shows: the CURRENT state, bounded. Its size depends on the size of the project (features, files),
    /// never on how long people and agents have worked: history stays in .agent/events.jsonl, one session's detail is a
    /// separate file loaded on demand. Agents never read it (they ask the CLI for short, computed answers).
    /// Three questions, in this order: what do I (the human) have to do? where is the project? what is abnormal?
    /// </summary>
    public sealed class Snapshot
    {
        public const int MaxSessions = 30;
        public const int MaxRecent = 30;
        public const int MaxFiles = 40;

        public int Version { get; set; } = 1;
        public string Project { get; set; }
        public string Generated { get; set; }
        public ChainStatus Chain { get; set; }
        public TaskView Task { get; set; }
        public Attention Attention { get; set; } = new Attention();
        public object Roadmap { get; set; }
        public string Core { get; set; } = "";                         // Docs/Plan/Core.md as written (the project's core idea)
        public ProjectNotesResult Notes { get; set; } = new ProjectNotesResult();  // Docs/Plan/Notes.md, newest first
        public Anomalies Anomalies { get; set; } = new Anomalies();
        public List<SessionSummary> Sessions { get; set; } = new List<SessionSummary>();
        public object Housekeeping { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>1. What the HUMAN has to do.</summary>
    public sealed class Attention
    {
        public List<Fix> HumanFixes { get; set; } = new List<Fix>();
        public List<Approval> Approvals { get; set; } = new List<Approval>();
        public List<string> PlanErrors { get; set; } = new List<string>();
    }

    public sealed class Approval
    {
        public string Kind { get; set; }      // task | requirement | art | proposal
        public string Id { get; set; }
        public string What { get; set; }
        public string Command { get; set; }
    }

    /// <summary>3. What is abnormal (most recent first, capped).</summary>
    public sealed class Anomalies
    {
        public List<AnomalyItem> Recent { get; set; } = new List<AnomalyItem>();
        public List<FileView> Files { get; set; } = new List<FileView>();
    }

    public sealed class AnomalyItem
    {
        public long Seq { get; set; }
        public string Ts { get; set; }
        public string Session { get; set; }
        public string Trust { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>One line per session (newest first).</summary>
    public sealed class SessionSummary
    {
        public string Id { get; set; }
        public string Started { get; set; }
        public string Ended { get; set; }
        public string Task { get; set; }
        public int Events { get; set; }
        public int Files { get; set; }
        public int Problems { get; set; }      // violations + denied + unobserved + stop blocked
        public string Gate { get; set; }       // last gate result in the session
        public string Claim { get; set; }      // the agent's last message (what it SAYS)
        public bool StopBlocked { get; set; }
        public string Verdict { get; set; }    // claim vs evidence
    }

    /// <summary>Everything about ONE session (separate file, loaded when the human clicks it).</summary>
    public sealed class SessionDetail
    {
        public string Id { get; set; }
        public List<LaneView> Lanes { get; set; } = new List<LaneView>();
        public List<StepView> Steps { get; set; } = new List<StepView>();
        public List<GateView> Gates { get; set; } = new List<GateView>();
        public List<ClaimView> Claims { get; set; } = new List<ClaimView>();
        public List<FileView> Files { get; set; } = new List<FileView>();
    }

    public static class SnapshotBuilder
    {
        /// <summary>One pass over the log; output bounded by MaxSessions/MaxRecent/MaxFiles.</summary>
        public static Snapshot Build(IReadOnlyList<AgentEvent> events, ChainStatus chain, AgentTask task, string generated, string project)
        {
            var s = new Snapshot { Generated = generated, Project = project, Chain = chain };
            if (task != null)
            {
                s.Task = new TaskView
                {
                    Id = task.Id, Title = task.Title, Status = task.Status, Scope = task.ScopeText,
                    Requested = task.HasPendingRequest ? string.Join(", ", task.RequestedFeatures.Select(f => "Features/" + f).Concat(task.RequestedPaths)) + (task.RequestedTestChanges ? " +test changes" : "") : null,
                    Reason = task.RequestReason
                };
                if (task.Status == AgentTask.PendingApproval || task.HasPendingRequest)
                    s.Attention.Approvals.Add(new Approval
                    {
                        Kind = "task", Id = task.Id, What = "Agent xin phạm vi: " + (s.Task.Requested ?? task.ScopeText) + (task.RequestReason != null ? " (" + task.RequestReason + ")" : ""),
                        Command = "./Tools/test-gate.sh task approve"
                    });
            }

            var sessions = new Dictionary<string, SessionSummary>(StringComparer.Ordinal);
            var sessionFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var recent = new Queue<AnomalyItem>();
            var files = new Dictionary<string, FileView>(StringComparer.Ordinal);
            int denied = 0, violations = 0, unobserved = 0, stopBlocked = 0, claims = 0, gatesPassed = 0, gatesFailed = 0, gatesPending = 0;

            foreach (var e in events)
            {
                string trust = Reducer.TrustOf(e);
                bool problem = trust == Trust.Violation || trust == Trust.Denied || trust == Trust.Unobserved || e.Type == EventTypes.StopBlocked;
                switch (e.Type)
                {
                    case EventTypes.PolicyDenied: denied++; break;
                    case EventTypes.UnobservedChange: if (trust == Trust.Unobserved) unobserved++; break;
                    case EventTypes.StopBlocked: stopBlocked++; break;
                    case EventTypes.Claim: claims++; break;
                    case EventTypes.GateFinished:
                        string r = e.Get("result");
                        if (r == "passed") gatesPassed++; else if (r == "pending") gatesPending++; else gatesFailed++;
                        break;
                }
                if (trust == Trust.Violation)
                    violations++;

                if (problem)
                {
                    recent.Enqueue(new AnomalyItem { Seq = e.Seq, Ts = e.Ts, Session = e.Session, Trust = trust, Title = Reducer.TitleOf(e), Detail = Trim(Reducer.Describe(e), 400) });
                    if (recent.Count > Snapshot.MaxRecent)
                        recent.Dequeue();
                }

                string path = e.Get("path");
                if (path != null && (e.Type == EventTypes.FileModified || e.Type == EventTypes.UnobservedChange) && !StepGroups.IsOutsideRepo(e))
                {
                    if (!files.TryGetValue(path, out var f))
                        files[path] = f = new FileView { Path = path };
                    f.Changes++;
                    f.LastSeq = e.Seq;
                    f.Via = e.Get("via") ?? e.Type.ToLowerInvariant();
                    f.Scope = e.Get("scope") ?? f.Scope;
                    f.Trust = trust;
                }
                else if (e.Type == EventTypes.GateFinished && e.Get("result") == "passed")
                    foreach (var f in files.Values.Where(x => x.Trust == Trust.Observed))
                        f.Trust = Trust.Verified;

                if (string.IsNullOrEmpty(e.Session) || e.Session == "cli")
                    continue;
                if (!sessions.TryGetValue(e.Session, out var ss))
                {
                    sessions[e.Session] = ss = new SessionSummary { Id = e.Session, Started = e.Ts };
                    sessionFiles[e.Session] = new HashSet<string>(StringComparer.Ordinal);
                }
                ss.Events++;
                ss.Ended = e.Ts;
                ss.Task = e.Task ?? ss.Task;
                if (problem)
                    ss.Problems++;
                if (e.Type == EventTypes.FileModified && path != null && !StepGroups.IsOutsideRepo(e))
                    sessionFiles[e.Session].Add(path);
                if (e.Type == EventTypes.GateFinished)
                    ss.Gate = e.Get("result");
                if (e.Type == EventTypes.StopBlocked)
                    ss.StopBlocked = true;
                if (e.Type == EventTypes.StopAllowed)
                    ss.StopBlocked = false;
                if (e.Type == EventTypes.Claim)
                    ss.Claim = Trim(e.Get("text"), 200);
            }

            foreach (var ss in sessions.Values)
            {
                ss.Files = sessionFiles[ss.Id].Count;
                ss.Verdict = ss.Claim == null ? null
                    : ss.StopBlocked || ss.Gate == "failed" ? "mâu thuẫn với bằng chứng"
                    : ss.Gate == null ? "không có bằng chứng"
                    : ss.Gate == "passed" ? (ss.Problems > 0 ? "có bằng chứng, kèm bất thường" : "có bằng chứng") : "một phần (Unity pending)";
            }

            s.Sessions = sessions.Values.OrderByDescending(x => x.Ended, StringComparer.Ordinal).Take(Snapshot.MaxSessions).ToList();
            s.Anomalies.Recent = recent.Reverse().ToList();
            s.Anomalies.Files = files.Values.Where(f => f.Trust == Trust.Violation || f.Trust == Trust.Unobserved)
                .OrderByDescending(f => f.LastSeq).Take(Snapshot.MaxFiles).ToList();
            s.Counts["events"] = events.Count;
            s.Counts["sessions"] = sessions.Count;
            s.Counts["denied"] = denied;
            s.Counts["violations"] = violations;
            s.Counts["unobserved"] = unobserved;
            s.Counts["stopBlocked"] = stopBlocked;
            s.Counts["claims"] = claims;
            s.Counts["gatesPassed"] = gatesPassed;
            s.Counts["gatesFailed"] = gatesFailed;
            s.Counts["gatesPending"] = gatesPending;
            s.Counts["filesFlagged"] = files.Values.Count(f => f.Trust == Trust.Violation || f.Trust == Trust.Unobserved);
            return s;
        }

        /// <summary>The detail of one session: the same deterministic projection (Reducer), only over that session's events.</summary>
        public static SessionDetail Detail(IReadOnlyList<AgentEvent> events, string session)
        {
            var mine = events.Where(e => e.Session == session).ToList();
            var m = Reducer.Build(mine, new ChainStatus { Ok = true }, null, null);
            return new SessionDetail { Id = session, Lanes = m.Lanes, Steps = m.Steps, Gates = m.Gates, Claims = m.Claims, Files = m.Files };
        }

        private static string Trim(string s, int n) => s == null || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
