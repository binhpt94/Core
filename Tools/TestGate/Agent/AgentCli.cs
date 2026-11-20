using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    /// <summary>
    ///   task start "&lt;what&gt;" --feature F [--feature G] [--path P] [--tests]   one feature = active at once; wider = waits for approval
    ///   task request --feature G | --path P | --tests "&lt;why&gt;"               ask for more scope (current scope keeps working)
    ///   task request --core "&lt;why&gt;"                                          core maintenance: owner-only files writable (human approves)
    ///   task need &lt;F-ID|Module|Core&gt; "&lt;what&gt;" [--api L]… [--patch P]  another module must change: a CR, never a chat message (CrCli)
    ///   task start … --from CR-n                                             work on an approved CR (= --req CR-n)
    ///   task approve                                                         OWNER: a terminal, or the owner's yes/no in Claude Code (OwnerAsk)
    ///   task show | task close
    ///   events verify | events tail [n] | reconcile
    ///   dash [--out file.html]      self-contained dashboard (open offline)
    ///   serve [--port 8765]         same page, live (re-reads the log every 2 s), localhost only, read-only
    /// </summary>
    public static class AgentCli
    {
        public static int Task(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "show";
            var task = TaskStore.Load(root);
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var features = Values(args, "--feature");
            var paths = Values(args, "--path");
            bool tests = args.Contains("--tests");
            bool core = args.Contains("--core");
            string text = args.Skip(2).Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && !IsValueOf(args, i + 2)).FirstOrDefault();

            switch (sub)
            {
                case "start":
                    if (task != null && task.Status != AgentTask.Closed)
                    {
                        Console.Error.WriteLine("Task " + task.Id + " is " + task.Status + ". Close it first (task close) or ask for more scope (task request).");
                        return 1;
                    }
                    var reqs = Values(args, "--req").Concat(Values(args, "--from")).Distinct().ToList();
                    var notes = new List<string>();
                    if (reqs.Count > 0)
                    {
                        var (modules, errors, rush) = TaskPlanLink.Prepare(root, reqs);
                        if (errors.Count > 0)
                        {
                            Console.Error.WriteLine("Cannot start: " + string.Join("; ", errors));
                            Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "task-cli").With("target", "task start " + string.Join(" ", reqs)).With("reason", string.Join("; ", errors)));
                            return 1;
                        }
                        features = features.Union(modules).ToList();
                        notes = rush;
                    }
                    if (string.IsNullOrWhiteSpace(text) || (features.Count == 0 && paths.Count == 0))
                    {
                        Console.Error.WriteLine("Usage: task start \"<what>\" (--req R-ID ... | --feature <Feature>) [--role tests|impl|arbiter] [--path P] [--tests]   (see: roadmap next)");
                        return 64;
                    }
                    string role = Value(args, "--role");
                    string roleError = RoleError(root, role, reqs);
                    if (roleError != null)
                    {
                        Console.Error.WriteLine("Cannot start: " + roleError);
                        Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "task-cli").With("target", "task start --role " + role).With("reason", roleError));
                        return 1;
                    }
                    var clash = TaskRegistry.Conflicts(root, features, role);
                    if (clash.Count > 0)
                    {
                        string why = string.Join("; ", clash.Select(c => c.Id + " in " + c.Worktree + " (modules " + string.Join(",", c.Modules.Intersect(features)) + ")"));
                        Console.Error.WriteLine("Cannot start: another worktree is working on the same modules: " + why + ". Pick another item from `roadmap next`.");
                        Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "task-cli").With("target", "task start").With("reason", "module conflict: " + why));
                        return 1;
                    }
                    var unknown = features.Where(f => !Directory.Exists(Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", f))).ToList();
                    task = ScopePolicy.Start("T" + DateTime.UtcNow.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture), text, features, paths, tests, now);
                    task.Baseline = WorkSnapshot.Take(root).ToDictionary(k => k.Key, k => k.Value);
                    task.Role = role;
                    var plan = Game.Architecture.Rules.PlanParser.Load(root);
                    foreach (var id in reqs)
                        task.Requirements[id] = plan.Requirement(id).Hash;
                    task.RushDebt = notes;
                    // Working on a CR filed in another worktree: its lines travel with this branch (the tests covering it need them).
                    var crs = CrStore.Load(root);
                    foreach (var id in reqs)
                        if (crs.TryGetValue(id, out var cr) && CrStore.Adopt(root, cr) > 0)
                            Console.WriteLine(id + ": copied from the shared CR log into " + Game.Architecture.Rules.CrLog.FileOf(cr.To) + " (commit it with your work)");
                    // Contracts grants: CRs this task works on, and approved API CRs this feature asked for that are not in place yet.
                    foreach (var g in CrStore.GrantsFor(root, reqs, CrCli.FromOf(plan, task)))
                        task.CrGrants[g.Key] = g.Value;
                    if (task.Provenance == "maintenance")
                        task.CoverBaseline = Provenance.CoverResults(root, task.Features);
                    TaskStore.Save(root, task);
                    TaskRegistry.Upsert(root, task, reqs);
                    Recorder.Emit(Recorder.New(EventTypes.TaskCreated, "task-cli").With("id", task.Id).With("title", text).With("status", task.Status)
                        .With("scope", task.ScopeText).With("baselineFiles", task.Baseline.Count.ToString()).With("role", role)
                        .With("requirements", reqs.Count > 0 ? string.Join(",", reqs) : null).With("rush", notes.Count > 0 ? string.Join(" | ", notes) : null));
                    foreach (var n in notes)
                        Console.WriteLine(n);
                    Console.WriteLine("Task " + task.Id + ": " + task.Status + " · scope " + task.ScopeText + (unknown.Count > 0 ? " (new feature: " + string.Join(", ", unknown) + ")" : ""));
                    if (task.Status == AgentTask.PendingApproval)
                        Console.WriteLine("More than one feature, explicit paths or test changes: a human must run ./Tools/test-gate.sh task approve");
                    return 0;

                case "request":
                    if (task == null || task.Status == AgentTask.Closed)
                    {
                        Console.Error.WriteLine("No open task.");
                        return 1;
                    }
                    ScopePolicy.Request(task, features, paths, tests, text, core);
                    TaskStore.Save(root, task);
                    Recorder.Emit(Recorder.New(EventTypes.TaskScopeRequested, "task-cli").With("id", task.Id)
                        .With("features", string.Join(",", task.RequestedFeatures)).With("paths", string.Join(",", task.RequestedPaths))
                        .With("tests", task.RequestedTestChanges ? "true" : "false")
                        .With("core", task.RequestedCoreMaintenance ? "true" : null).With("reason", text));
                    Console.WriteLine("Requested. Current scope stays " + task.ScopeText + ". Run ./Tools/test-gate.sh task approve alone: the owner answers yes/no (or approves in a terminal).");
                    return 0;

                case "approve":
                    return Approve(root, task, now, args);

                case "need":
                    return CrCli.Need(root, task, args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null,
                        args.Skip(3).Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && !IsValueOf(args, i + 3)).FirstOrDefault(),
                        Values(args, "--api"), Value(args, "--patch"));

                case "close":
                    if (task == null || task.Status == AgentTask.Closed)
                    {
                        Console.WriteLine("No open task.");
                        return 0;
                    }
                    var lastGate = EventLog.For(root).ReadAll().LastOrDefault(e => e.Type == EventTypes.GateFinished);
                    var summary = TestSummary.ForTask(root, task);
                    if (summary.Count > 0)
                    {
                        Console.WriteLine("Tests this task added or changed (what they check):");
                        foreach (var l in summary)
                            Console.WriteLine("  " + l);
                        Recorder.Emit(Recorder.New(EventTypes.TaskTestSummary, "task-cli").With("id", task.Id).With("summary", string.Join("\n", summary)));
                    }
                    task.Status = AgentTask.Closed;
                    TaskStore.Save(root, task);
                    TaskRegistry.Remove(root, task.Id);
                    Recorder.Emit(Recorder.New(EventTypes.TaskClosed, "task-cli").With("id", task.Id).With("lastGate", lastGate?.Get("result") ?? "never run"));
                    Console.WriteLine("Closed " + task.Id + " (last gate: " + (lastGate?.Get("result") ?? "never run") + ")");
                    return 0;

                default:
                    if (task == null)
                        Console.WriteLine("No task. Start one: ./Tools/test-gate.sh task start \"<what>\" --feature <Feature>");
                    else
                    {
                        foreach (var st in TaskPlanLink.Stale(Game.Architecture.Rules.PlanParser.Load(root), task))
                            Console.WriteLine("STALE " + st);
                        if (task.Requirements.Count > 0)
                            Console.WriteLine("requirements: " + string.Join(", ", task.Requirements.Keys));
                        Console.WriteLine(task.Id + " \"" + task.Title + "\" " + task.Status + " · scope " + task.ScopeText +
                                          (task.HasPendingRequest ? " · requested: " + string.Join(", ", task.RequestedFeatures.Concat(task.RequestedPaths)) + (task.RequestedTestChanges ? " +tests" : "") + (task.RequestedCoreMaintenance ? " +core maintenance" : "") + " (" + task.RequestReason + ")" : ""));
                    }
                    return 0;
            }
        }

        /// <summary>What `task approve` would grant (shown in the terminal and in the owner's yes/no dialog).</summary>
        public static (string text, string error) DescribeApprove(AgentTask task)
        {
            if (task == null || (task.Status != AgentTask.PendingApproval && !task.HasPendingRequest))
                return (null, "nothing to approve");
            var lines = new List<string>
            {
                "Task " + task.Id + " \"" + task.Title + "\"",
                "  current scope : " + task.ScopeText + (task.Status == AgentTask.PendingApproval ? " (not active yet)" : "")
            };
            if (task.HasPendingRequest)
                lines.Add("  requested     : " + string.Join(", ", task.RequestedFeatures.Select(f => "Features/" + f).Concat(task.RequestedPaths)) +
                          (task.RequestedTestChanges ? " + permission to change test expectations" : "") +
                          (task.RequestedCoreMaintenance ? " + CORE MAINTENANCE (owner-only files: Tools/, rules, .claude/, .github/, docs, PublicApi...)" : "") + "  (" + task.RequestReason + ")");
            return (string.Join("\n", lines), null);
        }

        private static int Approve(string root, AgentTask task, string now, string[] args)
        {
            var (text, nothing) = DescribeApprove(task);
            if (nothing != null)
            {
                Console.WriteLine("Nothing to approve.");
                return 0;
            }
            // Owner only: a human at a terminal, or the owner's yes in Claude Code (OwnerAsk grant). Claude Code shells carry CLAUDECODE=1.
            var auth = OwnerAsk.Authorize(root, args, "task-cli", "approving a task's scope is the owner's.");
            if (auth == null)
                return 3;
            Console.WriteLine(text);
            if (auth.Human)
            {
                Console.Write("Type the task id to approve: ");
                if ((Console.ReadLine() ?? "").Trim() != task.Id)
                {
                    Console.WriteLine("Not approved.");
                    return 1;
                }
            }
            ScopePolicy.Approve(task, now);
            TaskStore.Save(root, task);
            TaskRegistry.Upsert(root, task, task.Requirements.Keys);
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(EventTypes.TaskApproved, auth.Source).With("id", task.Id).With("scope", task.ScopeText).With("toolUseId", auth.ToolUseId));
            Console.WriteLine("Approved: " + task.ScopeText);
            return 0;
        }

        /// <summary>Can this role work these requirements now? Roles exist only for [critical] requirements; impl needs live seals (no open dispute); arbiter needs something to rule on.</summary>
        private static string RoleError(string root, string role, List<string> reqs)
        {
            if (role == null)
            {
                var critical = reqs.Where(id => TestSeal.IsCritical(Game.Architecture.Rules.PlanParser.Load(root), id)).ToList();
                return critical.Count == 0 ? null
                    : string.Join(", ", critical) + ": [critical], so its tests and its code come from separate sessions: start with --role tests (roadmap next prints the command)";
            }
            if (!TestRoles.All.Contains(role))
                return "--role is one of: " + string.Join(", ", TestRoles.All);
            if (reqs.Count == 0)
                return "--role " + role + " needs the requirements it works on (--req R-ID ...)";
            var plan = Game.Architecture.Rules.PlanParser.Load(root);
            var plain = reqs.Where(id => !TestSeal.IsCritical(plan, id)).ToList();
            if (plain.Count > 0)
                return string.Join(", ", plain) + ": not [critical], so no --role: write the tests and the code in one task " +
                       "(task start \"<what>\" --req " + string.Join(" --req ", plain) + "). Roles are for [critical] requirements only.";
            var seals = SealStore.Load(root);
            foreach (var id in reqs)
            {
                seals.TryGetValue(id, out var s);
                bool live = s != null && !s.Void;
                if (role == TestRoles.Impl && (!live || s.OpenDispute || s.Ask || s.Suspect))
                    return id + (live ? " is disputed or being re-checked: an arbiter settles it first" : " has no sealed tests yet: a --role tests session writes and seals them first");
                if (role == TestRoles.Arbiter && !(live && (s.OpenDispute || s.Suspect)))
                    return id + ": nothing to rule on (no open dispute, not marked for re-check)";
            }
            return null;
        }

        public static int Events(string root, string[] args)
        {
            var log = EventLog.For(root);
            string sub = args.Length > 1 ? args[1] : "verify";
            if (sub == "tail")
            {
                int n = args.Length > 2 && int.TryParse(args[2], out var x) ? x : 20;
                foreach (var e in log.ReadAll().Skip(Math.Max(0, log.ReadAll().Count - n)))
                    Console.WriteLine("#" + e.Seq + " " + e.Ts.Substring(11, 8) + " " + e.Type + " [" + e.Source + "] " +
                                      string.Join(" ", e.Data.Where(kv => kv.Key != "detail").Select(kv => kv.Key + "=" + AgentHooks.Trim(kv.Value, 60))));
                return 0;
            }
            var status = log.Verify();
            Console.WriteLine((status.Ok ? "OK " : "BROKEN ") + status.Message + (status.Ok ? " · head " + status.Head : ""));
            return status.Ok ? 0 : 1;
        }

        public static int Reconcile(string root)
        {
            var current = WorkSnapshot.Take(root);
            var u = WorkSnapshot.Unexplained(current, WorkSnapshot.Explained(EventLog.For(root).ReadAll()));
            foreach (var (path, known, actual) in u)
            {
                Recorder.Emit(Recorder.New(EventTypes.UnobservedChange, "git-observer").With("path", path).With("before", known).With("after", actual).With("note", "reconcile"));
                Console.WriteLine("UNOBSERVED " + path + " (" + known + " -> " + actual + ")");
            }
            Console.WriteLine(u.Count == 0 ? "Every change on disk is explained by the event log." : u.Count + " change(s) no observer saw.");
            return 0;
        }

        // ------------------------------------------------------------------ dashboard

        // Layout: the VIEW is static (Dashboard: index.html, style.css, app.js; edit it directly, nothing generates it).
        // The DATA is written by `dash`: Dashboard/Data/data.json (+ data.js = the same as `window.MODEL = ...`, so the
        // page also opens with a double-click, where browsers refuse to fetch a .json) and sessions/<id>.js (one session's
        // detail, loaded when clicked). `dash --bundle` = one self-contained HTML to send to someone.
        public const string ViewDir = "Dashboard";
        public const string DataDir = "Dashboard/Data";
        private static readonly System.Text.Json.JsonSerializerOptions Json = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

        public static (Snapshot snapshot, List<AgentEvent> events) Model(string root)
        {
            var log = EventLog.For(root);
            var (events, chain) = log.ReadVerified();
            var s = SnapshotBuilder.Build(events, chain, TaskStore.Load(root), DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), Path.GetFileName(Path.GetFullPath(root)));
            try
            {
                var roadmap = RoadmapView.Build(root);
                s.Roadmap = roadmap;
                s.Attention.PlanErrors.AddRange(roadmap.Errors);
                foreach (var g in roadmap.Gaps.Where(g => g.Actor == "human" && g.Kind == "NEEDS_REVIEW"))
                    s.Attention.Approvals.Add(new Approval { Kind = "requirement", Id = g.Requirement, What = g.Requirement + ": tự kiểm tra rồi chấp nhận (" + g.Feature + ")", Command = "./Tools/test-gate.sh roadmap accept " + g.Requirement });
                // Only finals that passed the automatic art gate wait for the owner; candidates never do.
                foreach (var sl in roadmap.Slots.Where(x => x.BoundAs == "final" && x.Gate == "passed" && (x.Level == "CANDIDATE" || x.Level == "STALE")))
                    s.Attention.Approvals.Add(new Approval { Kind = "art", Id = sl.Id, What = sl.Id + ": " + (sl.Level == "STALE" ? "bản final đã đổi sau khi duyệt, cần xem lại" : "bản final chờ duyệt") + " (" + sl.BoundFile + ")", Command = "./Tools/test-gate.sh art accept " + sl.Id });
                foreach (var c in ApiBaseline.PendingAll(root))
                    s.Attention.Approvals.Add(new Approval { Kind = "api", Id = c.Group, What = "API công khai của " + c.Group + " đã đổi (+" + c.Added.Count + " / -" + c.Removed.Count + (c.Breaking ? ", CÓ PHÁ VỠ" : ", chỉ thêm") + "): bảo AI chạy lệnh rồi bấm Yes/No", Command = "./Tools/test-gate.sh api accept " + c.Group });
                foreach (var p in roadmap.Proposals)
                    s.Attention.Approvals.Add(new Approval { Kind = "proposal", Id = p, What = "Agent đề xuất đổi plan", Command = "xem Docs/Plan/Inbox/" + p + ", sửa Docs/Plan/features.md nếu đồng ý, rồi xóa đề xuất" });
            }
            catch (Exception e)
            {
                s.Attention.PlanErrors.Add("roadmap unavailable: " + e.Message);
            }
            try
            {
                var hk = TestGate.Files.FilesView.Build(root, events);
                if (hk.Loose.Count > 100)
                    hk.Loose = hk.Loose.Take(100).ToList();
                s.Housekeeping = hk;
            }
            catch (Exception e)
            {
                s.Housekeeping = new TestGate.Files.FilesView { Violations = { "files unavailable: " + e.Message } };
            }
            s.Attention.HumanFixes = Attribution.Load(root);
            // The Dashboard's two hand-written sources (an AI edits them on request; the page only reads).
            string core = Path.Combine(root, "Docs", "Plan", "Core.md"), notes = Path.Combine(root, ProjectNotes.RelativePath);
            s.Core = File.Exists(core) ? File.ReadAllText(core) : "";
            if (File.Exists(notes))
                s.Notes = ProjectNotes.Parse(File.ReadAllText(notes));
            return (s, events);
        }

        public static string ToJson(object o) => System.Text.Json.JsonSerializer.Serialize(o, Json);

        /// <summary>JSON inside a &lt;script&gt;: never let data close the tag.</summary>
        private static string ScriptSafe(string json) => json.Replace("</", "<\\/");

        public static string SessionFileName(string session) =>
            "s-" + (string.IsNullOrEmpty(session) ? "x" : new string(session.Where(char.IsLetterOrDigit).Take(40).ToArray())) + ".js";

        public static int Dash(string root, string[] args)
        {
            var (s, events) = Model(root);
            string dataDir = Path.Combine(root, DataDir);
            string sessDir = Path.Combine(dataDir, "Sessions");
            Directory.CreateDirectory(sessDir);

            string only = Value(args, "--session");
            if (only != null)
            {
                string f = Path.Combine(sessDir, SessionFileName(only));
                File.WriteAllText(f, SessionJs(events, only));
                Console.WriteLine("Session detail: " + f);
                return 0;
            }

            string json = ToJson(s);
            File.WriteAllText(Path.Combine(dataDir, "data.json"), json);
            File.WriteAllText(Path.Combine(dataDir, "data.js"), "window.MODEL = " + ScriptSafe(json) + ";\n");
            var keep = new HashSet<string>(s.Sessions.Select(x => SessionFileName(x.Id)));
            foreach (var old in Directory.GetFiles(sessDir, "s-*.js").Where(f => !keep.Contains(Path.GetFileName(f))))
                File.Delete(old);                                   // bounded: only the sessions the page lists
            foreach (var ss in s.Sessions)
                File.WriteAllText(Path.Combine(sessDir, SessionFileName(ss.Id)), SessionJs(events, ss.Id));

            if (args.Contains("--bundle"))
            {
                string outPath = Value(args, "--out") ?? Path.Combine(root, "Dashboard", "Data", "dashboard.html");
                File.WriteAllText(outPath, Bundle(root, s, events));
                Console.WriteLine("Bundle (one file, to send): " + outPath);
            }
            Console.WriteLine("Dashboard: open " + Path.Combine(root, ViewDir, "index.html") + "   (data: " + DataDir + "/data.json, " + (json.Length / 1024) + " KB)");
            var c = s.Counts;
            Console.WriteLine("chờ người " + (s.Attention.HumanFixes.Count + s.Attention.Approvals.Count + s.Attention.PlanErrors.Count) +
                              " · events " + c["events"] + " · chain " + (s.Chain.Ok ? "intact" : "BROKEN at #" + s.Chain.BrokenAt) +
                              " · violations " + c["violations"] + " · denied " + c["denied"] + " · unobserved " + c["unobserved"] +
                              " · gates passed/failed " + c["gatesPassed"] + "/" + c["gatesFailed"]);
            return 0;
        }

        private static string SessionJs(List<AgentEvent> events, string session) =>
            "(window.SESSION_DETAIL = window.SESSION_DETAIL || {})[" + ToJson(session) + "] = " + ScriptSafe(ToJson(SnapshotBuilder.Detail(events, session))) + ";\n";

        /// <summary>One HTML with the view, the data and the listed sessions' detail inlined (to attach or send).</summary>
        public static string Bundle(string root, Snapshot s, List<AgentEvent> events)
        {
            string view = Path.Combine(root, ViewDir);
            string html = File.ReadAllText(Path.Combine(view, "index.html"));
            var data = new StringBuilder("window.MODEL = " + ScriptSafe(ToJson(s)) + ";\n");
            foreach (var ss in s.Sessions)
                data.Append(SessionJs(events, ss.Id));
            return html
                .Replace("<link rel=\"stylesheet\" href=\"style.css\">", "<style>\n" + File.ReadAllText(Path.Combine(view, "style.css")) + "</style>")
                .Replace("<script src=\"Data/data.js\"></script>","<script>\n" + data + "</script>")
                .Replace("<script src=\"app.js\"></script>", "<script>\nwindow.BUNDLED = true;\n" + File.ReadAllText(Path.Combine(view, "app.js")) + "</script>");
        }

        /// <summary>Live view: serves the static view and fresh data (regenerated on each data request). Read-only, localhost.</summary>
        public static int Serve(string root, string[] args)
        {
            int port = int.TryParse(Value(args, "--port"), out var p) ? p : 8765;
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + port + "/");
            listener.Start();
            Console.WriteLine("Dashboard: http://localhost:" + port + "/" + ViewDir + "/index.html  (read-only; Ctrl+C to stop)");
            string full = Path.GetFullPath(root);
            while (true)
            {
                var ctx = listener.GetContext();
                try
                {
                    string url = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).TrimStart('/');
                    if (url == "")
                    {
                        ctx.Response.Redirect("/" + ViewDir + "/index.html");
                        continue;
                    }
                    if (url == DataDir + "/data.json" || url == DataDir + "/data.js")
                        Dash(root, new string[0]);
                    string file = Path.GetFullPath(Path.Combine(full, url));
                    bool allowed = (file.StartsWith(Path.GetFullPath(Path.Combine(full, ViewDir)) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                    || file.StartsWith(Path.GetFullPath(Path.Combine(full, DataDir)) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) && File.Exists(file);
                    if (!allowed)
                    {
                        ctx.Response.StatusCode = 404;
                        continue;
                    }
                    var bytes = File.ReadAllBytes(file);
                    string ext = Path.GetExtension(file);
                    ctx.Response.ContentType = ext == ".json" ? "application/json; charset=utf-8" : ext == ".js" ? "text/javascript; charset=utf-8"
                        : ext == ".css" ? "text/css; charset=utf-8" : "text/html; charset=utf-8";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("serve: " + e.Message);
                }
                finally
                {
                    ctx.Response.Close();
                }
            }
        }

        // ------------------------------------------------------------------ args

        private static List<string> Values(string[] args, string name)
        {
            var list = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    list.Add(args[i + 1]);
            return list;
        }

        private static string Value(string[] args, string name) => Values(args, name).FirstOrDefault();

        private static bool IsValueOf(string[] args, int index) =>
            index > 0 && (args[index - 1] == "--feature" || args[index - 1] == "--path" || args[index - 1] == "--out" || args[index - 1] == "--port" || args[index - 1] == "--session" || args[index - 1] == "--req" || args[index - 1] == "--role"
                             || args[index - 1] == "--from" || args[index - 1] == "--api" || args[index - 1] == "--patch");
    }
}
