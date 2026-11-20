using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    public sealed class OwnerAskDecision
    {
        public const string Ask = "ask";
        public const string Deny = "deny";
        public string Kind { get; set; }
        public string Reason { get; set; }
        /// <summary>The gate arguments exactly as the CLI will receive them (owner CLI commands), null for a baseline rewrite.</summary>
        public string[] Args { get; set; }
        public bool Baseline { get; set; }
        /// <summary>Hash of exactly what the dialog showed (API lines, policy line); the CLI refuses if what it would write differs.</summary>
        public string Bind { get; set; }
        public static OwnerAskDecision Denied(string why) => new OwnerAskDecision { Kind = Deny, Reason = why };
    }

    /// <summary>Who authorised an owner command: a human at a terminal, or the owner's yes in Claude Code (a consumed grant).</summary>
    public sealed class OwnerAuth
    {
        public bool Human { get; set; }
        public string ToolUseId { get; set; }
        /// <summary>The Bind of the grant the owner said yes to (null for a human terminal).</summary>
        public string Bound { get; set; }
        public string Source => Human ? "human-terminal" : OwnerAsk.Source;
    }

    /// <summary>
    /// Owner commands (task approve, roadmap accept, art accept, cr decide CR-n, tests unseal|grandfather, api accept, policy vendor,
    /// mcp allow) and baseline rewrites
    /// (GOLDEN_WRITE=1 / LAYOUT_WRITE=1) run in an agent's shell only after the owner's yes in Claude Code:
    ///   PreToolUse   only the plain form (one command, no chaining/expansion) is asked; the dialog text is computed by the gate
    ///                (what exactly is decided). An invalid target is blocked, the owner is not asked. Anything else stays blocked.
    ///                CLI commands: a one-shot grant (sha256 of a nonce, the exact args, expiry) goes into the hash-chained event
    ///                log, and the command Claude Code runs is prefixed with GAME_OWNER_GRANT=&lt;nonce&gt; (updatedInput).
    ///   CLI          a human terminal works as before; an agent shell needs a live grant for these exact args (consumed once).
    ///   Next Pre     an unconsumed grant of this session (the owner said no, or it never ran) is revoked.
    ///   PostToolUse  the tool ran = the owner said yes: baseline files the command changed are recorded as approved (content hash).
    /// </summary>
    public static class OwnerAsk
    {
        public const string Source = "owner-ask";
        public const string AskedEvent = "OWNER_COMMAND_ASKED";
        public const string ApprovedEvent = "OWNER_COMMAND_APPROVED";
        public const string GrantUsedEvent = "OWNER_GRANT_USED";
        public const string GrantRevokedEvent = "OWNER_GRANT_REVOKED";
        public const string GrantVariable = "GAME_OWNER_GRANT";
        /// <summary>Time to read the dialog. The main guard is RevokeStale (a grant dies at the next tool call); this is a backstop.</summary>
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        /// <summary>Replaceable in tests.</summary>
        public static Func<DateTime> Clock = () => DateTime.UtcNow;
        private const string PendingFile = "owner-pending";

        private static readonly Regex Gate = new Regex(@"^(?:\./)?Tools/test-gate\.sh$");
        // An assignment the shell would really make (start, after a separator or blank, after export/env), not a quoted mention
        // (grep 'GOLDEN_WRITE=1'). Evading it gains nothing: a baseline written by an unasked command is out of scope at Stop, and a
        // grant needs the nonce, which only the hook knows.
        private const string AssignAt = @"(?:^|[;&|(`]\s*|\s|\b(?:export|env)\s+)";
        private static readonly Regex BaselineAssignment = new Regex(AssignAt + @"(?:GOLDEN_WRITE|LAYOUT_WRITE)\s*=");
        private static readonly Regex GrantAssignment = new Regex(AssignAt + GrantVariable + @"\s*=");
        private static readonly string[][] OwnerSubs =
        {
            new[] { "task", "approve" }, new[] { "roadmap", "accept" }, new[] { "art", "accept" }, new[] { "cr", "decide" },
            new[] { "tests", "unseal" }, new[] { "tests", "grandfather" },
            new[] { "api", "accept" }, new[] { "policy", "vendor" }, new[] { "mcp", "allow" },
        };

        // ------------------------------------------------------------------ PreToolUse

        /// <summary>null = not an owner command / baseline rewrite (other rules apply); otherwise ask or deny.</summary>
        public static OwnerAskDecision Decide(string root, string command)
        {
            string cmd = (command ?? "").Trim();
            bool owner = AgentHooks.IsOwnerCommand(cmd);
            bool baseline = BaselineAssignment.IsMatch(cmd);
            if (GrantAssignment.IsMatch(cmd))
                return OwnerAskDecision.Denied(GrantVariable + " is set only by the gate (the owner's yes in Claude Code)");
            if (!owner && !baseline)
                return null;
            var tokens = Tokenize(cmd);
            string alone = "run it alone, as one plain command (no ;, &&, |, $(…), bash -c, redirection), so the owner is asked yes/no";
            if (tokens == null)
                return OwnerAskDecision.Denied(alone);
            if (baseline)
                return DecideBaseline(root, tokens) ?? OwnerAskDecision.Denied(alone + ": GOLDEN_WRITE=1 dotnet test Tools/LogicTests … | LAYOUT_WRITE=1 ./Tools/run-unity-tests.sh …");
            if (tokens.Count < 3 || !Gate.IsMatch(tokens[0]) || !OwnerSubs.Any(s => s[0] == tokens[1] && s[1] == tokens[2]))
                return OwnerAskDecision.Denied(alone);
            var args = tokens.Skip(1).ToArray();
            var (_, error) = Describe(root, args);
            if (error != null)
                return OwnerAskDecision.Denied(error + " (the owner is not asked)");
            return new OwnerAskDecision { Kind = OwnerAskDecision.Ask, Args = args, Reason = Explain(root, args), Bind = BindOf(root, args) };
        }

        private static OwnerAskDecision DecideBaseline(string root, List<string> t)
        {
            if (t.Count >= 4 && t[0] == "GOLDEN_WRITE=1" && t[1] == "dotnet" && t[2] == "test" && t[3].TrimEnd('/') == "Tools/LogicTests")
                return new OwnerAskDecision { Kind = OwnerAskDecision.Ask, Baseline = true, Reason = OwnerAskText.GoldenWrite(root) };
            if (t.Count >= 2 && t[0] == "LAYOUT_WRITE=1" && Regex.IsMatch(t[1], @"^(?:\./)?Tools/run-unity-tests\.sh$"))
                return new OwnerAskDecision { Kind = OwnerAskDecision.Ask, Baseline = true, Reason = OwnerAskText.LayoutWrite(root, t.Skip(2)) };
            return null;
        }

        /// <summary>What the owner decides, computed from the gate's own state; (null, error) when there is nothing valid to decide.</summary>
        public static (string text, string error) Describe(string root, string[] args)
        {
            string A(int i) => args.Length > i ? args[i] : null;
            string head = args[0] + " " + args[1];
            switch (head)
            {
                case "task approve":
                    return args.Length == 2 ? AgentCli.DescribeApprove(TaskStore.Load(root)) : (null, "usage: task approve");
                case "roadmap accept":
                    return args.Length == 3 ? RoadmapCli.DescribeAccept(root, A(2)) : (null, "usage: roadmap accept R-ID");
                case "art accept":
                    if (A(2) == "--draft")
                        return args.Length == 4 ? ArtAcceptDraftCli.Describe(root, A(3)) : (null, "usage: art accept --draft \"FILE\"");
                    return args.Length == 3 ? ArtCli.DescribeAccept(root, A(2)) : (null, "usage: art accept SLOT-ID | art accept --draft \"FILE\"");
                case "cr decide":
                    if (args.Length == 2)
                        return (null, "the cr decide loop is for a terminal; here decide one CR: cr decide CR-n approve|reject \"<why>\"");
                    return args.Length == 5 || args.Length == 4 ? CrCli.DescribeDecide(root, A(2), A(3), A(4)) : (null, "usage: cr decide CR-n approve|reject \"<why>\"");
                case "tests unseal":
                    return args.Length == 4 ? TestSeal.DescribeUnseal(root, A(2), A(3)) : (null, "usage: tests unseal R-ID \"<why>\"");
                case "tests grandfather":
                    return args.Length == 2 ? TestSeal.DescribeGrandfather(root) : (null, "usage: tests grandfather");
                case "api accept":
                    return args.Length == 3 ? ApiBaseline.Describe(root, A(2)) : (null, "usage: api accept <Game.Core|Game.<Feature>.Contracts>");
                case "policy vendor":
                case "mcp allow":
                    var (plan, error) = PlanOf(root, args);
                    return plan == null ? (null, error) : (plan.Text, null);
            }
            return (null, "not an owner command");
        }

        private static (PolicyCli.Plan plan, string error) PlanOf(string root, string[] args) =>
            args[0] == "policy" ? PolicyCli.PlanVendor(root, args.Skip(2).ToArray())
            : args.Length == 4 ? PolicyCli.PlanMcp(root, args[2], args[3]) : (null, "usage: mcp allow read|write|exec <tool|'prefix*'>");

        /// <summary>What the grant is bound to: the exact content the dialog showed (null = the args say it all).</summary>
        private static string BindOf(string root, string[] args)
        {
            switch (args[0] + " " + args[1])
            {
                case "api accept": return ApiBaseline.Pending(root, args[2]).change?.Hash;
                case "art accept": return args.Length == 4 && args[2] == "--draft" ? ArtAcceptDraftCli.Evaluate(root, args[3]).result?.Bind : null;
                case "policy vendor":
                case "mcp allow": return PlanOf(root, args).plan?.Bind;
            }
            return null;
        }

        /// <summary>The owner's dialog text (plain Vietnamese, OwnerAskText) for a command Describe found valid.</summary>
        public static string Explain(string root, string[] args)
        {
            string A(int i) => args.Length > i ? args[i] : null;
            switch (args[0] + " " + args[1])
            {
                case "task approve":
                    return OwnerAskText.TaskApprove(TaskStore.Load(root));
                case "roadmap accept":
                    return OwnerAskText.RoadmapAccept(root, Game.Architecture.Rules.PlanParser.Load(root).Requirement(A(2)));
                case "art accept":
                    if (A(2) == "--draft")
                        return OwnerAskText.ArtAcceptDraft(ArtAcceptDraftCli.Evaluate(root, A(3)).result, A(3));
                    return OwnerAskText.ArtAccept(ArtStatus.Compute(root)[A(2)]);
                case "cr decide":
                    return OwnerAskText.CrDecide(CrStore.Load(root)[A(2)], A(3) == "approve", A(4));
                case "tests unseal":
                    return OwnerAskText.TestsUnseal(Game.Architecture.Rules.PlanParser.Load(root)?.Requirement(A(2)), SealStore.Load(root)[A(2)], A(3));
                case "tests grandfather":
                    var plan = Game.Architecture.Rules.PlanParser.Load(root);
                    return OwnerAskText.TestsGrandfather(TestSeal.Grandfather(root, null, write: false).Messages
                        .Select(m => { var id = m.Split(' ')[0]; var r = plan?.Requirement(id); return r == null ? m : "\"" + r.Text + "\" [" + id + "]"; }).ToList());
            }
            return Describe(root, args).text;
        }

        /// <summary>Plain words and quoted strings only; null when the shell would do more than run one command with literal args.</summary>
        public static List<string> Tokenize(string cmd)
        {
            var tokens = new List<string>();
            var cur = new StringBuilder();
            bool inToken = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '\'' || c == '"')
                {
                    int end = cmd.IndexOf(c, i + 1);
                    if (end < 0)
                        return null;
                    string inner = cmd.Substring(i + 1, end - i - 1);
                    if (inner.IndexOfAny(new[] { '$', '`', '\\', '\n', '\r', '!' }) >= 0)
                        return null;
                    cur.Append(inner);
                    inToken = true;
                    i = end;
                }
                else if (c == ' ' || c == '\t')
                {
                    if (inToken)
                        tokens.Add(cur.ToString());
                    cur.Clear();
                    inToken = false;
                }
                else if (char.IsLetterOrDigit(c) || "-_./=:,+@%".IndexOf(c) >= 0)
                {
                    cur.Append(c);
                    inToken = true;
                }
                else
                    return null; // ; & | < > ( ) $ ` \ * ? [ ] { } ~ # ! newline …
            }
            if (inToken)
                tokens.Add(cur.ToString());
            return tokens.Count == 0 ? null : tokens;
        }

        /// <summary>The ask for a CLI command: grant recorded, command prefixed with the nonce. Returns the hook's JSON.</summary>
        public static string AskCli(string root, HookInput h, OwnerAskDecision d)
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            string nonce = Convert.ToHexString(bytes).ToLowerInvariant();
            string grant = Sha(nonce);
            long expires = new DateTimeOffset(Clock()).ToUnixTimeSeconds() + (long)Ttl.TotalSeconds;
            Recorder.Emit(Recorder.New(AskedEvent, "hook").With("kind", "cli").With("toolUseId", h.ToolUseId).With("grant", grant)
                .With("key", Key(d.Args)).With("bind", d.Bind).With("expires", expires.ToString(CultureInfo.InvariantCulture)).With("what", AgentHooks.Trim(d.Reason, 600)));
            AddPending(root, grant);
            return AskJson(d.Reason, UpdatedInput(h, Commented(d.Reason, GrantVariable + "=" + nonce + " " + h.Input("command").Trim()), Question(d.Reason)));
        }

        /// <summary>The ask for a baseline rewrite (no grant: the tests don't check the shell; Post records what it wrote).</summary>
        public static string AskBaseline(HookInput h, OwnerAskDecision d)
        {
            Recorder.Emit(Recorder.New(AskedEvent, "hook").With("kind", "baseline").With("toolUseId", h.ToolUseId)
                .With("what", AgentHooks.Trim(d.Reason, 600)));
            return AskJson(d.Reason, UpdatedInput(h, Commented(d.Reason, h.Input("command").Trim()), Question(d.Reason)));
        }

        /// <summary>
        /// The dialog text as bash comment lines above the command: the permission dialog shows the command in a monospace box that
        /// keeps line breaks (the description and the reason don't). Every line starts with "# " and holds no newline or backslash,
        /// so nothing in it can run; the last line is the command itself.
        /// </summary>
        public static string Commented(string text, string command)
        {
            var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => ("# " + l.Replace('\\', '/')).TrimEnd());
            return string.Join("\n", lines) + "\n" + command;
        }

        /// <summary>The command line that really runs (the last line of a commented command).</summary>
        public static string RunLine(string command) => (command ?? "").Replace("\r", "").Split('\n').Last().Trim();

        private static string Question(string text) => (text ?? "").Split('\n')[0].Trim();

        private static string AskJson(string reason, Dictionary<string, object> updatedInput)
        {
            var specific = new Dictionary<string, object>
            {
                ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "ask", ["permissionDecisionReason"] = reason
            };
            if (updatedInput != null)
                specific["updatedInput"] = updatedInput;
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["hookSpecificOutput"] = specific });
        }

        /// <summary>
        /// The tool's whole input (updatedInput replaces it) with the commented command and the gate's question as its description:
        /// the permission dialog shows the command and its description, not always the hook's reason (VS Code). The agent's own
        /// description is dropped, so it can't word the owner's question.
        /// </summary>
        private static Dictionary<string, object> UpdatedInput(HookInput h, string command, string description)
        {
            var input = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(h.ToolInputJson))
                foreach (var p in JsonDocument.Parse(h.ToolInputJson).RootElement.EnumerateObject())
                    input[p.Name] = p.Value.Clone();
            input["command"] = command;
            input["description"] = description;
            return input;
        }

        /// <summary>Every PreToolUse: grants this session left unconsumed (the owner said no) die before the next tool call runs.</summary>
        public static void RevokeStale(string root, HookInput h)
        {
            string p = Path.Combine(root, ".agent", PendingFile);
            if (!File.Exists(p) || new FileInfo(p).Length == 0)
                return;
            var events = EventLog.For(root).ReadAll();
            var closed = new HashSet<string>(events.Where(e => e.Type == GrantUsedEvent || e.Type == GrantRevokedEvent).Select(e => e.Get("grant")));
            foreach (var asked in events.Where(e => e.Type == AskedEvent && e.Get("grant") != null && !closed.Contains(e.Get("grant"))))
                if (asked.Session == h.Session || Expired(asked))
                    Recorder.Emit(Recorder.New(GrantRevokedEvent, "hook").With("grant", asked.Get("grant")).With("toolUseId", asked.Get("toolUseId"))
                        .With("reason", Expired(asked) ? "expired" : "not run before the next tool call (the owner said no)"));
            File.WriteAllText(p, "");
        }

        private static void AddPending(string root, string grant)
        {
            Directory.CreateDirectory(Path.Combine(root, ".agent"));
            File.AppendAllText(Path.Combine(root, ".agent", PendingFile), grant + "\n");
        }

        // ------------------------------------------------------------------ CLI

        /// <summary>Human terminal, or a live grant for exactly these args (consumed); otherwise refused (printed + POLICY_DENIED), null.</summary>
        public static OwnerAuth Authorize(string root, string[] args, string cli, string refusal)
        {
            if (AgentShell.IsHumanTerminal)
                return new OwnerAuth { Human = true };
            var auth = TryConsume(root, args, Environment.GetEnvironmentVariable(GrantVariable), out string why);
            if (auth != null)
                return auth;
            Console.Error.WriteLine("Refused: " + refusal + " Needs a human at a terminal, or the owner's yes in Claude Code: run "
                                    + "./Tools/test-gate.sh " + string.Join(" ", args) + " alone (" + why + ").");
            Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, cli).With("target", string.Join(" ", args)).With("reason", why));
            return null;
        }

        public static OwnerAuth TryConsume(string root, string[] args, string nonce, out string why)
        {
            if (string.IsNullOrEmpty(nonce))
            {
                why = "no owner grant: not an interactive human terminal";
                return null;
            }
            string grant = Sha(nonce.Trim());
            var events = EventLog.For(root).ReadAll();
            var asked = events.LastOrDefault(e => e.Type == AskedEvent && e.Get("grant") == grant);
            if (asked == null)
                why = "unknown owner grant";
            else if (events.Any(e => (e.Type == GrantUsedEvent || e.Type == GrantRevokedEvent) && e.Get("grant") == grant))
                why = "owner grant already used or revoked (one yes = one run)";
            else if (Expired(asked))
                why = "owner grant expired (" + (int)Ttl.TotalMinutes + " min)";
            else if (asked.Get("key") != Key(args))
                why = "owner grant is for other arguments";
            else
            {
                why = null;
                Recorder.Emit(Recorder.New(GrantUsedEvent, Source).With("grant", grant).With("toolUseId", asked.Get("toolUseId")).With("key", asked.Get("key")));
                return new OwnerAuth { Human = false, ToolUseId = asked.Get("toolUseId"), Bound = asked.Get("bind") };
            }
            return null;
        }

        private static bool Expired(AgentEvent asked) =>
            !long.TryParse(asked.Get("expires"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long exp) ||
            new DateTimeOffset(Clock()).ToUnixTimeSeconds() > exp;

        public static string Key(IEnumerable<string> args) => string.Join("\u001f", args);

        public static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        // ------------------------------------------------------------------ PostToolUse

        /// <summary>The tool ran = the owner said yes. Baseline files that command changed become approved content.</summary>
        public static void AfterRun(string root, HookInput h, IEnumerable<(string path, string before, string after)> changes)
        {
            if (h.ToolUseId == null)
                return;
            var asked = EventLog.For(root).ReadAll().LastOrDefault(e => e.Type == AskedEvent && e.Get("toolUseId") == h.ToolUseId);
            if (asked == null)
                return;
            Recorder.Emit(Recorder.New(ApprovedEvent, "hook").With("toolUseId", h.ToolUseId).With("kind", asked.Get("kind")));
            Func<string, bool> mine = asked.Get("kind") == "baseline" ? (Func<string, bool>)IsBaseline : IsOwnerRecord;
            foreach (var (path, _, after) in changes.Where(c => mine(c.path)))
                Recorder.Emit(Recorder.New(ApprovedEvent, "hook").With("toolUseId", h.ToolUseId).With("kind", "owner-file").With("path", path).With("after", after));
        }

        public static bool IsBaseline(string rel) => rel != null && (rel.Contains("/Tests/Replays/") || rel.Contains("/Tests/Layout/"));

        /// <summary>Owner-only records an owner CLI command writes (seal and CR logs prove their own writes: SealStore/CrStore).</summary>
        public static bool IsOwnerRecord(string rel) => rel == "Docs/Plan/accepted.txt" || rel == "Docs/Plan/art-accepted.txt"
            || rel == "Tools/ArchitecturePolicy.txt" || rel == McpPolicy.File_ || (rel != null && rel.StartsWith("UnityProject/Assets/_Game/", StringComparison.Ordinal) && rel.EndsWith("/PublicApi.txt", StringComparison.Ordinal));

        /// <summary>Is this content (snapshot hash) of an owner file one that an owner-approved command (yes/no) produced?</summary>
        public static bool IsApprovedBaseline(string root, string rel, string state) =>
            (IsBaseline(rel) || IsOwnerRecord(rel)) &&
            EventLog.For(root).ReadAll().Any(e => e.Type == ApprovedEvent && e.Get("kind") == "owner-file" && e.Get("path") == rel && e.Get("after") == state);
    }
}
