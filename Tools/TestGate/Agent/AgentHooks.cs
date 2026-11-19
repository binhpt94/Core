using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>Claude Code hook input (fields read defensively: missing ones are null).</summary>
    public sealed class HookInput
    {
        public string Session { get; private set; }
        public string Agent { get; private set; }
        public string Event { get; private set; }
        public string ToolName { get; private set; }
        public string ToolUseId { get; private set; }
        public string TranscriptPath { get; private set; }
        public string Source { get; private set; }
        public string LastMessage { get; private set; }
        public Dictionary<string, string> ToolInput { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public static HookInput Parse(string json)
        {
            var h = new HookInput();
            if (string.IsNullOrWhiteSpace(json))
                return h;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    h.Session = S("session_id");
                    h.Agent = S("agent_type") ?? S("agent_id");
                    h.Event = S("hook_event_name");
                    h.ToolName = S("tool_name");
                    h.ToolUseId = S("tool_use_id");
                    h.TranscriptPath = S("transcript_path");
                    h.Source = S("source");
                    h.LastMessage = S("last_assistant_message");
                    if (r.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object)
                        foreach (var p in ti.EnumerateObject())
                            h.ToolInput[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                }
            }
            catch (JsonException)
            {
                // malformed input: treat as empty (never break the agent on our parsing)
            }
            return h;
        }

        public string Input(string key) => ToolInput.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Emits events with the current hook context. Recording must never crash a hook.</summary>
    public static class Recorder
    {
        public static string Root { get; set; }
        public static string Session { get; set; } = "cli";
        public static string Agent { get; set; } = "main";

        public static void Use(HookInput h)
        {
            Session = h.Session ?? Session;
            Agent = h.Agent ?? "main";
        }

        public static AgentEvent New(string type, string source)
        {
            AgentTask task = null;
            try { task = TaskStore.Load(Root); } catch (Exception) { }
            return new AgentEvent { Type = type, Source = source, Session = Session, Agent = Agent, Task = task?.Id };
        }

        public static void Emit(AgentEvent e)
        {
            if (Root == null)
                return;
            try { EventLog.For(Root).Append(e); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static class AgentHooks
    {
        private static readonly HashSet<string> WriteTools = new HashSet<string> { "Edit", "MultiEdit", "Write", "NotebookEdit" };
        private static readonly HashSet<string> SpawnTools = new HashSet<string> { "Task", "Agent" };
        /// <summary>Talking to another agent/session: logged with the sender's task (a request made this way is never an approval).</summary>
        private static readonly HashSet<string> MessageTools = new HashSet<string> { "SendMessage" };

        public static string Rel(string root, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            string r = Path.GetRelativePath(root, full).Replace('\\', '/');
            return r.StartsWith("..", StringComparison.Ordinal) ? null : r;
        }

        private static string PrePath(string root, HookInput h) =>
            Path.Combine(root, ".agent", "pre-" + Safe(h.Session) + "-" + Safe(h.ToolUseId ?? h.Agent ?? "main") + ".json");

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "x" : new string(s.Where(char.IsLetterOrDigit).Take(40).ToArray());

        // ------------------------------------------------------------------ PreToolUse: block BEFORE it happens

        public static int Pre(string root, HookInput h)
        {
            Recorder.Use(h);
            var task = TaskStore.Load(root);
            RememberSession(root, h);

            // Separation of duties: the session that wrote/sealed (or disputed) a requirement's tests can't write its code or rule on it.
            if (task != null && task.IsActive && (task.Role == TestRoles.Impl || task.Role == TestRoles.Arbiter) &&
                (WriteTools.Contains(h.ToolName ?? "") || h.ToolName == "Bash" || McpPolicy.IsMcp(h.ToolName)))
            {
                string conflict = TestSeal.SessionConflict(root, task, h.Session);
                if (conflict != null)
                {
                    Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", h.ToolName).With("target", "role " + task.Role).With("reason", conflict));
                    Console.Error.WriteLine("BLOCKED: " + conflict);
                    return 2;
                }
            }

            if (WriteTools.Contains(h.ToolName ?? ""))
            {
                string rel = Rel(root, h.Input("file_path") ?? h.Input("notebook_path"));
                if (PlanEditGate.IsGated(rel))
                    return PrePlan(root, h, rel);  // before scope: even a core-maintenance task asks the owner for the plan / core idea
                var d = ScopePolicy.Check(task, rel);
                if (d.Allowed)
                    return 0;
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", h.ToolName).With("target", rel).With("reason", d.Reason));
                Console.Error.WriteLine("BLOCKED by the scope guard: " + d.Reason);
                return 2;
            }

            if (h.ToolName == "Bash")
            {
                string cmd = h.Input("command") ?? "";
                string why = ForbiddenCommand(cmd);
                if (why != null)
                {
                    Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", "Bash").With("target", Trim(cmd, 200)).With("reason", why));
                    Console.Error.WriteLine("BLOCKED: " + why);
                    return 2;
                }
                // Observe what this command changes on disk (sed/echo/scripts bypass the Edit tool).
                Directory.CreateDirectory(Path.Combine(root, ".agent"));
                File.WriteAllText(PrePath(root, h), WorkSnapshot.ToJson(WorkSnapshot.Take(root)));
                return 0;
            }

            if (McpPolicy.IsMcp(h.ToolName))
                return PreMcp(root, h, task);

            if (MessageTools.Contains(h.ToolName ?? ""))
            {
                Recorder.Emit(Recorder.New(EventTypes.AgentMessage, "hook").With("to", h.Input("to") ?? h.Input("recipient"))
                    .With("summary", Trim(h.Input("summary") ?? h.Input("message"), 200))
                    .With("fromTask", task != null && task.IsActive ? task.Id : null)
                    .With("fromScope", task != null && task.IsActive ? task.ScopeText : null));
                Console.Error.WriteLine("Note: asking another session to change its module is not an approval. Need a change in another module? " +
                                        "./Tools/test-gate.sh task need <F-ID|Module> \"<what, why>\" (a CR; keep working on a fake meanwhile).");
                return 0;
            }

            if (SpawnTools.Contains(h.ToolName ?? ""))
                Recorder.Emit(Recorder.New(EventTypes.AgentSpawned, "hook")
                    .With("agent", h.Input("subagent_type") ?? "general-purpose")
                    .With("description", Trim(h.Input("description") ?? h.Input("prompt"), 200)));
            return 0;
        }

        /// <summary>The agent's session for gate commands run through Bash (`tests seal`/`dispute`/`rule` attribute by it).</summary>
        private static void RememberSession(string root, HookInput h)
        {
            if (string.IsNullOrEmpty(h.Session))
                return;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, ".agent"));
                File.WriteAllText(Path.Combine(root, ".agent", TestSeal.SessionFile), h.Session);
            }
            catch (IOException) { }
        }

        /// <summary>Tests/arbiter sessions may add only a skeleton to Logic/ or Contracts/ (default bodies), never the solution.</summary>
        private static List<string> SkeletonFindings(string root, AgentTask task, string rel)
        {
            if (task == null || !task.IsActive || (task.Role != TestRoles.Tests && task.Role != TestRoles.Arbiter))
                return new List<string>();
            var feature = task.Features.FirstOrDefault(f => TestRoles.IsSkeletonPath(ScopePolicy.Game + "Features/" + f + "/", rel));
            if (feature == null)
                return new List<string>();
            var (code, before) = AgentGit.Run(root, "show HEAD:" + rel);
            string full = Path.Combine(root, rel);
            string after = File.Exists(full) ? File.ReadAllText(full) : "";
            return TestRoles.SkeletonViolations(code == 0 ? before : null, after).Take(5)
                .Select(v => "SKELETON " + rel + ": " + v + " (role " + task.Role + " writes tests; the impl session writes the code)").ToList();
        }

        /// <summary>
        /// A task without provenance (no --req R-/CR-, not core maintenance) is maintenance: it may refactor, fix tooling, tidy; it may
        /// not change what a feature promises (its Contracts, its Domain rules). Such a change must trace to a requirement or an
        /// approved CR, whoever asked for it: that is what makes "please change X for me" from another session visible.
        /// </summary>
        public static List<string> Untraced(AgentTask task, string rel)
        {
            var list = new List<string>();
            if (task == null || !task.IsActive || task.Provenance != "maintenance" || rel == null || !rel.EndsWith(".cs", StringComparison.Ordinal))
                return list;
            var m = System.Text.RegularExpressions.Regex.Match(rel, "^" + ScopePolicy.Game + @"Features/([^/]+)/(Contracts|Logic/Domain)/");
            if (m.Success && !CrStore.IsGranted(task, rel, out _))
                list.Add("UNTRACED_CHANGE " + rel + ": changes what " + m.Groups[1].Value + " promises (" + m.Groups[2].Value + "/) in a task with no requirement. " +
                         "Start the task from the plan or a CR (task start … --req R-…|--from CR-n); another module needing it files `task need`. " +
                         "Pure refactor? Revert the behaviour change, or ask the human.");
            return list;
        }

        /// <summary>features.md: checked by the gate, then the owner answers yes/no in Claude Code (PlanEditGate).</summary>
        private static int PrePlan(string root, HookInput h, string rel)
        {
            var d = PlanEditGate.Decide(root, h);
            if (d.Kind == PlanEditDecision.Deny)
            {
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", h.ToolName).With("target", rel).With("reason", Trim(d.Reason, 300)));
                Console.Error.WriteLine("BLOCKED: " + d.Reason);
                return 2;
            }
            Recorder.Emit(Recorder.New(PlanEditGate.AskedEvent, "hook").With("tool", h.ToolName).With("path", rel)
                .With("toolUseId", h.ToolUseId).With("proposed", d.ProposedHash).With("changes", Trim(d.Reason, 600)));
            Console.WriteLine(PlanEditGate.AskJson(d));
            return 0;
        }

        /// <summary>
        /// Unity/Blender MCP tools change files from inside another process: same treatment as Bash (git snapshot before/after),
        /// so a prefab made through MCP is the AGENT's change (scope, per-file rules, Stop), never "unobserved" (= human).
        /// </summary>
        private static int PreMcp(string root, HookInput h, AgentTask task)
        {
            var kind = McpPolicy.Classify(h.ToolName, McpPolicy.Load(root));
            string why = null;
            if (kind == McpKind.Deny)
                why = h.ToolName + " is denied by " + McpPolicy.File_;
            else if (kind == McpKind.Exec)
                why = h.ToolName + " runs arbitrary code in the editor/DCC: its effects can't be scoped or observed (it can write outside the repo). "
                      + "Use a specific tool (create/modify/...) or ask the human to allow it in " + McpPolicy.File_;
            else if (kind == McpKind.Write || kind == McpKind.ExecAllowed)
            {
                if (task == null || !task.IsActive)
                    why = "no active task: start one before changing the project through " + h.ToolName + " (./Tools/test-gate.sh task start ...)";
                else
                    foreach (var t in McpPolicy.TargetPaths(h))
                    {
                        string rel = Rel(root, t);
                        if (rel == null)
                            continue;
                        var d = ScopePolicy.Check(task, rel);
                        if (!d.Allowed)
                        {
                            why = rel + ": " + d.Reason;
                            break;
                        }
                    }
            }
            if (why != null)
            {
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", h.ToolName).With("target", Trim(string.Join(" ", McpPolicy.TargetPaths(h)), 200)).With("reason", why));
                Console.Error.WriteLine("BLOCKED: " + why);
                return 2;
            }
            if (kind != McpKind.Read)
            {
                Directory.CreateDirectory(Path.Combine(root, ".agent"));
                File.WriteAllText(PrePath(root, h), WorkSnapshot.ToJson(WorkSnapshot.Take(root)));
            }
            return 0;
        }

        /// <summary>
        /// Owner-only gate subcommands, matched only where the gate is really invoked (test-gate.sh, the TestGate binary, `dotnet run
        /// --project Tools/TestGate`), also inside bash -c, pipes and chains. Mentioning one in text (a commit message, a doc) is not running it.
        /// </summary>
        private static readonly (Regex rx, string why)[] OwnerCommands = new[]
        {
            (@"art\s+accept", "approving art is a human decision (./Tools/test-gate.sh art accept in a terminal)"),
            (@"roadmap\s+accept", "accepting a requirement is a human decision (./Tools/test-gate.sh roadmap accept in a terminal)"),
            (@"task\s+approve", "approving a task's scope is for a human in a terminal (./Tools/test-gate.sh task approve)"),
            (@"cr\s+decide", "deciding change requests is the owner's (./Tools/test-gate.sh cr decide in a terminal)"),
            (@"tests\s+(?:unseal|grandfather)", "unsealing / grandfathering tests is the owner's last resort (a human at a terminal)"),
        }.Select(x => (new Regex(@"(?:test-gate\.sh|TestGate(?:\.dll|\.exe)?|Tools[/\\]TestGate)[""']?\s+(?:-\S*\s+)*" + x.Item1 + @"\b"), x.Item2)).ToArray();

        /// <summary>Commands no agent may run: touching the event store/task by hand, approving its own scope.</summary>
        public static string ForbiddenCommand(string cmd)
        {
            string c = cmd.Trim();
            bool viaGate = c.StartsWith("./Tools/test-gate.sh", StringComparison.Ordinal) || c.StartsWith("Tools/test-gate.sh", StringComparison.Ordinal);
            foreach (var (rx, why) in OwnerCommands)
                if (rx.IsMatch(c))
                    return why;
            if (!viaGate && c.Contains(".agent"))
                return ".agent/ (event log, task) is written only by the test gate";
            return null;
        }

        // ------------------------------------------------------------------ PostToolUse: record what really happened

        /// <param name="checkFiles">per-file architecture rules (same as before): returns violation lines</param>
        public static int Post(string root, HookInput h, Func<IEnumerable<string>, List<string>> checkFiles)
        {
            Recorder.Use(h);
            var task = TaskStore.Load(root);
            var feedback = new List<string>();

            if (WriteTools.Contains(h.ToolName ?? ""))
            {
                string rel = Rel(root, h.Input("file_path") ?? h.Input("notebook_path"));
                if (rel == null)
                    return 0;
                if (PlanEditGate.IsGated(rel))
                    PlanEditGate.AfterWrite(root, h, rel);   // the tool ran = the owner said yes to this exact text
                RecordFile(root, task, rel, h.ToolName, null, null, feedback);
                feedback.AddRange(checkFiles(new[] { rel }));
            }
            else if (h.ToolName == "Bash" || McpPolicy.IsMcp(h.ToolName) && File.Exists(PrePath(root, h)))
            {
                string pre = PrePath(root, h);
                var before = File.Exists(pre) ? WorkSnapshot.FromJson(File.ReadAllText(pre)) : null;
                if (File.Exists(pre))
                    File.Delete(pre);
                bool mcp = h.ToolName != "Bash";
                string cmd = mcp ? h.ToolName + " " + string.Join(" ", h.ToolInput.Select(kv => kv.Key + "=" + kv.Value)) : h.Input("command") ?? "";
                var changes = before == null ? new List<(string, string, string)>() : WorkSnapshot.Diff(before, WorkSnapshot.Take(root));
                Recorder.Emit(Recorder.New(mcp ? EventTypes.ToolUsed : EventTypes.CommandRun, "hook").With("tool", h.ToolName).With("command", Trim(cmd, 300)).With("changedFiles", changes.Count.ToString())
                    .With("observer", before == null ? "no pre-snapshot (not observed)" : "git snapshot before/after").With("mcpWrite", mcp ? "true" : null));
                foreach (var (path, b, a) in changes)
                    RecordFile(root, task, path, mcp ? h.ToolName : "Bash", Trim(cmd, 160), b + " -> " + a, feedback, a);
                var codeFiles = changes.Where(c => c.Item3 != WorkSnapshot.Deleted).Select(c => c.Item1).ToList();
                if (codeFiles.Count > 0)
                    feedback.AddRange(checkFiles(codeFiles)); // Bash edits get the same per-file rules as Edit
            }
            // Reading (Read/Grep/Glob/...) changes nothing and is most of all tool calls: not logged (the log stays small; the
            // session's transcript has it if ever needed).
            else if (!string.IsNullOrEmpty(h.ToolName) && !SpawnTools.Contains(h.ToolName) && !MessageTools.Contains(h.ToolName) && !Reducer.Browsing.Contains(h.ToolName))
            {
                string target = h.Input("file_path") ?? h.Input("pattern") ?? h.Input("path") ?? h.Input("url") ?? h.Input("query") ?? h.Input("description");
                Recorder.Emit(Recorder.New(EventTypes.ToolUsed, "hook").With("tool", h.ToolName).With("target", Trim(Rel(root, target) ?? target, 200)));
            }

            if (feedback.Count == 0)
                return 0;
            Console.Error.WriteLine("Fix this now (the change is recorded):");
            foreach (var f in feedback.Distinct().Take(30))
                Console.Error.WriteLine(f);
            return 2;
        }

        private static void RecordFile(string root, AgentTask task, string rel, string via, string command, string change, List<string> feedback, string after = null)
        {
            if (WorkSnapshot.Ignored(rel))
                return;
            string full = Path.Combine(root, rel);
            string state = after ?? (File.Exists(full) ? WorkSnapshot.HashFile(full) : WorkSnapshot.Deleted);
            // Undoing a change (back to HEAD, or to how it was when the task started) is always allowed.
            bool restored = state == WorkSnapshot.Clean || (task != null && task.Baseline.TryGetValue(rel, out var bh) && bh == state);
            var d = restored ? ScopeDecision.Allow("restored to its original content")
                : PlanEditGate.IsApproved(root, rel, state) ? ScopeDecision.Allow("plan change the owner approved (yes/no)")
                : SealStore.IsGateWrite(root, rel, state) ? ScopeDecision.Allow("seal log written by the gate (tests …)")
                : CrStore.IsGateWrite(root, rel, state) ? ScopeDecision.Allow("CR log written by the gate (task need / cr …)")
                : ScopePolicy.Check(task, rel);
            string impact;
            try
            {
                var i = new ChangeImpact(root).Classify(rel);
                impact = string.Join("+", i.Roles) + (i.Views.Count > 0 ? " [" + string.Join(", ", i.Views) + "]" : "");
            }
            catch (Exception) { impact = "?"; }

            Recorder.Emit(Recorder.New(EventTypes.FileModified, WriteTools.Contains(via) ? "hook" : "git-observer")
                .With("path", rel).With("via", via).With("command", command).With("change", change)
                .With("after", state)
                .With("inScope", d.Allowed ? "true" : "false").With("scope", d.Reason).With("impact", impact));
            if (!d.Allowed)
            {
                Recorder.Emit(Recorder.New(EventTypes.PolicyViolation, "git-observer").With("rule", "scope").With("path", rel).With("reason", d.Reason));
                feedback.Add("SCOPE " + rel + " was changed by " + via + " but " + d.Reason + ". Revert it (git checkout -- " + rel + ") or ask for scope.");
            }
            else if (!restored)
                foreach (var s in SkeletonFindings(root, task, rel).Concat(CrStore.GrantFindings(root, task, rel)).Concat(Untraced(task, rel)))
                {
                    Recorder.Emit(Recorder.New(EventTypes.PolicyViolation, "git-observer").With("rule", s.Split(' ')[0].ToLowerInvariant()).With("path", rel).With("reason", s));
                    feedback.Add(s);
                }
        }

        // ------------------------------------------------------------------ SessionStart / SubagentStop

        public static int SessionStart(string root, HookInput h)
        {
            Recorder.Use(h);
            var log = EventLog.For(root);
            var (events, chain) = log.ReadVerified();
            Recorder.Emit(Recorder.New(EventTypes.SessionStarted, "hook").With("source", h.Source ?? "startup"));

            // Changes made while no observer was running (IDE, another tool, a human): honest, not hidden.
            var current = WorkSnapshot.Take(root);
            var unexplained = WorkSnapshot.Unexplained(current, WorkSnapshot.Explained(events));
            foreach (var (path, known, actual) in unexplained)
                Recorder.Emit(Recorder.New(events.Count == 0 ? EventTypes.FileBaseline : EventTypes.UnobservedChange, "git-observer")
                    .With("path", path).With("before", known).With("after", actual).With("note", events.Count == 0 ? "dirty before recording started" : "changed between sessions"));
            File.WriteAllText(Path.Combine(root, ".agent", "session-" + Safe(h.Session) + ".json"), WorkSnapshot.ToJson(current));

            // SessionStart stdout becomes context: the agent's memory is the project's state, not its own.
            var task = TaskStore.Load(root);
            var lastGate = events.LastOrDefault(e => e.Type == EventTypes.GateFinished);
            Console.WriteLine("[agent-control] task: " + (task == null || task.Status == AgentTask.Closed
                ? "none. Before editing: ./Tools/test-gate.sh task start \"<what>\" --feature <Feature>"
                : task.Id + " \"" + task.Title + "\" " + task.Status + ", scope " + task.ScopeText));
            Console.WriteLine("[agent-control] last gate: " + (lastGate == null ? "never run" : lastGate.Get("result") + " (#" + lastGate.Seq + " " + lastGate.Ts + ")"));
            Console.WriteLine("[agent-control] event log: " + chain.Message);
            var waiting = Attribution.Load(root);
            if (waiting.Count > 0)
                Console.WriteLine("[agent-control] waiting for a HUMAN (" + waiting.Count + ", not yours: don't fix or work around, mention in your report): " +
                                  string.Join("; ", waiting.Take(2).Select(f => f.Problem.Length > 90 ? f.Problem.Substring(0, 90) + "…" : f.Problem)));
            try
            {
                var plan = PlanParser.Load(root);
                if (plan != null)
                {
                    var report = TestGate.Planning.PlanStatus.Compute(TestGate.Planning.PlanInputs.Load(root));
                    Console.WriteLine("[roadmap] " + string.Join(" · ", report.Features.GroupBy(f => f.Status).Select(g => g.Count() + " " + g.Key)) +
                                      " · gaps: " + report.Gaps.Count(g => g.Actor == "agent") + " agent / " + report.Gaps.Count(g => g.Actor == "human") + " human");
                    foreach (var st in TestGate.Planning.TaskPlanLink.Stale(plan, task != null && task.IsActive ? task : null))
                        Console.WriteLine("[roadmap] STALE task: " + st);
                    foreach (var i in TestGate.Planning.PlanScheduler.Next(report).Where(x => x.Lane == "now").Take(3))
                        Console.WriteLine("[roadmap] next: " + i.Feature + " " + string.Join(",", i.Requirements.Take(4)) + " → " + i.Command);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[roadmap] unavailable: " + e.Message);
            }
            if (events.Count > 0 && unexplained.Count > 0)
                Console.WriteLine("[agent-control] " + unexplained.Count + " file(s) changed outside any observed session: " + string.Join(", ", unexplained.Take(5).Select(u => u.Path)));
            return 0;
        }

        public static int SubagentStop(string root, HookInput h)
        {
            Recorder.Use(h);
            Recorder.Emit(Recorder.New(EventTypes.AgentStopped, "hook"));
            return 0;
        }

        // ------------------------------------------------------------------ Stop: checks the agent can't argue with

        /// <summary>Reconcile + scope of every changed file + test-weakening guard. Returns blocking findings.</summary>
        public static List<string> StopChecks(string root, HookInput h, Func<string, string> baseContent) => StopChecks(root, h, baseContent, null);

        /// <param name="human">receives problems caused by a human's edits (not blocking the agent), with human commands</param>
        public static List<string> StopChecks(string root, HookInput h, Func<string, string> baseContent, List<Fix> human)
        {
            Recorder.Use(h);
            var findings = new List<string>();
            var events = EventLog.For(root).ReadAll();
            var current = WorkSnapshot.Take(root);

            // Changes no observer saw are not attributed to the agent (often the human in the IDE, e.g. editing the plan):
            // they are recorded as UNOBSERVED and shown, but scope/test checks below only judge what the agent was seen doing.
            // Exception: Unity keeps MCP edits in memory and may write the prefab/scene AFTER the tool call returned. If this
            // session changed the project through MCP, a late Unity-serialized file is most likely the agent's: it is judged as
            // the agent's (an unexplained red is never pushed onto the human).
            bool mcpWrote = events.Any(e => e.Session == h.Session && e.Get("mcpWrite") == "true");
            var unobserved = new HashSet<string>(StringComparer.Ordinal);
            var lateEditor = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, known, actual) in WorkSnapshot.Unexplained(current, WorkSnapshot.Explained(events)))
            {
                bool likelyAgent = mcpWrote && Attribution.IsUnitySerialized(path);
                (likelyAgent ? lateEditor : unobserved).Add(path);
                Recorder.Emit(Recorder.New(EventTypes.UnobservedChange, "git-observer").With("path", path).With("before", known).With("after", actual)
                    .With("note", likelyAgent ? "saved by the editor after this session's MCP edits: judged as the agent's" : "on disk at Stop, no observed tool call explains it")
                    .With("likely", likelyAgent ? "agent" : null));
            }

            var authors = Attribution.Authors(events);
            foreach (var p in lateEditor)
                authors[p] = "agent";
            foreach (var kv in authors.Where(kv => kv.Value == "human" && !lateEditor.Contains(kv.Key)))
                unobserved.Add(kv.Key);
            var task = TaskStore.Load(root);
            string sessionFile = Path.Combine(root, ".agent", "session-" + Safe(h.Session) + ".json");
            var baseline = task != null && task.IsActive ? task.Baseline
                : File.Exists(sessionFile) ? WorkSnapshot.FromJson(File.ReadAllText(sessionFile)).ToDictionary(k => k.Key, k => k.Value) : new Dictionary<string, string>();

            foreach (var kv in current)
            {
                if (baseline.TryGetValue(kv.Key, out var b) && b == kv.Value)
                    continue; // already like this before the task/session: not this agent's change
                // Scope judges what the agent was SEEN doing; a change no observer saw (human in the IDE) is UNOBSERVED, not a scope breach.
                var d = PlanEditGate.IsApproved(root, kv.Key, kv.Value) ? ScopeDecision.Allow("plan change the owner approved (yes/no)")
                    : SealStore.IsGateWrite(root, kv.Key, kv.Value) ? ScopeDecision.Allow("seal log written by the gate (tests …)")
                    : CrStore.IsGateWrite(root, kv.Key, kv.Value) ? ScopeDecision.Allow("CR log written by the gate (task need / cr …)")
                    : ScopePolicy.Check(task, kv.Key);
                if (!d.Allowed && !unobserved.Contains(kv.Key))
                    findings.Add("SCOPE " + kv.Key + ": " + d.Reason);
                else if (d.Allowed && !unobserved.Contains(kv.Key))
                    findings.AddRange(SkeletonFindings(root, task, kv.Key).Concat(CrStore.GrantFindings(root, task, kv.Key)).Concat(Untraced(task, kv.Key)));
                // Weakened tests are judged whoever changed them: a fake fix must not slip through an unobserved channel.
                if (TestGuard.IsTestFile(kv.Key) && (task == null || !task.AllowTestChanges))
                {
                    string full = Path.Combine(root, kv.Key);
                    bool byHuman = unobserved.Contains(kv.Key) || Attribution.AuthorOf(kv.Key, authors) == "human";
                    foreach (var f in TestGuard.Check(kv.Key, baseContent(kv.Key), File.Exists(full) ? File.ReadAllText(full) : null))
                    {
                        if (byHuman && human != null)
                            human.Add(new Fix { Who = "human", Problem = "TESTS " + f, Hint = Attribution.TestGuardHint(kv.Key), Files = { kv.Key } });
                        else
                            findings.Add("TESTS " + f + " (weakening tests needs `task request --tests` + human approval)");
                    }
                }
            }
            // Leftovers this session created (scene/temp/copy/misplaced): name them with the exact command, before the gate does.
            var mine = new HashSet<string>(events.Where(e => e.Type == EventTypes.FileModified && e.Session == h.Session && e.Get("path") != null)
                .Select(e => e.Get("path")), StringComparer.Ordinal);
            foreach (var path in current.Keys.Where(p => mine.Contains(p) && current[p] != WorkSnapshot.Deleted))
                foreach (var v in FileRules.CheckPath(path))
                    findings.Add("JUNK " + path + " (created/changed by you this session): " + v.Message + " → rm \"" + path + "\" (or move it)");

            // The human changed the plan under this task: finishing it against the old wording would be wrong.
            foreach (var st in TestGate.Planning.TaskPlanLink.Stale(PlanParser.Load(root), task != null && task.IsActive ? task : null))
            {
                findings.Add("STALE task " + task.Id + ": " + st + ". Re-read the requirement; adapt the work (and its tests), then `task close` and start a new task with --req.");
                Recorder.Emit(Recorder.New(EventTypes.TaskStale, "testgate").With("id", task.Id).With("reason", st));
            }
            foreach (var f in findings)
                Recorder.Emit(Recorder.New(EventTypes.PolicyViolation, "testgate").With("rule", f.Split(' ')[0].ToLowerInvariant()).With("reason", f));
            return findings;
        }

        /// <summary>What the agent says it did: recorded as a CLAIM (untrusted), shown next to the evidence.</summary>
        public static void RecordClaim(HookInput h)
        {
            string text = h.LastMessage ?? LastAssistantText(h.TranscriptPath);
            if (!string.IsNullOrWhiteSpace(text))
                Recorder.Emit(Recorder.New(EventTypes.Claim, "agent").With("text", Trim(text.Trim(), 600)));
        }

        private static string LastAssistantText(string transcript)
        {
            try
            {
                if (string.IsNullOrEmpty(transcript) || !File.Exists(transcript))
                    return null;
                foreach (var line in File.ReadLines(transcript).Reverse().Take(200))
                {
                    using (var doc = JsonDocument.Parse(line))
                    {
                        var r = doc.RootElement;
                        if (!r.TryGetProperty("type", out var t) || t.GetString() != "assistant" || !r.TryGetProperty("message", out var msg))
                            continue;
                        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                            continue;
                        var texts = content.EnumerateArray()
                            .Where(c => c.TryGetProperty("type", out var ct) && ct.GetString() == "text" && c.TryGetProperty("text", out _))
                            .Select(c => c.GetProperty("text").GetString()).ToList();
                        if (texts.Count > 0)
                            return string.Join("\n", texts);
                    }
                }
            }
            catch (Exception)
            {
                // transcript format is not a contract: best effort only
            }
            return null;
        }

        public static string Trim(string s, int n) => s == null ? null : s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
