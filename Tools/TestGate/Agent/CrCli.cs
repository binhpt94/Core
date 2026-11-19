using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>
    ///   task need &lt;F-ID|Module|Core&gt; "&lt;what, why&gt;" [--api "&lt;PublicApi line&gt;"]… [--patch .scratch/x.diff]
    ///       a module never asks another one by chat: the need becomes CR-n (the asker keeps working on a fake; its feature owes CR-n)
    ///   cr list [--all] | cr show CR-n
    ///   cr rule CR-n keep|reject|ask "&lt;why&gt;"      arbiter: a session other than the asker (breaking / core-additive CRs)
    ///   cr withdraw CR-n "&lt;why&gt;"                  the asker no longer needs it (not once approved)
    ///   cr deliver CR-n "&lt;what&gt;"                  a core-maintenance task did a Core CR
    ///   cr decide                                   HUMAN ONLY: the owner answers every CR waiting for them in one pass
    /// </summary>
    public static class CrCli
    {
        private static string Now => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public static int Need(string root, AgentTask task, string target, string text, List<string> api, string patch)
        {
            if (task == null || !task.IsActive)
            {
                Console.Error.WriteLine("No active task: a CR belongs to the work that needs it (task start … first).");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Usage: task need <F-ID|Module|Core> \"<what you need and why>\" [--api \"<PublicApi line>\"]… [--patch .scratch/x.diff]");
                return 64;
            }
            var plan = PlanParser.Load(root);
            var (to, module, error) = Resolve(plan, target);
            if (error != null)
            {
                Console.Error.WriteLine("Cannot file: " + error);
                return 1;
            }
            string from = FromOf(plan, task);
            if (from == to || task.Features.Contains(module))
            {
                Console.Error.WriteLine("Cannot file: " + module + " is in your own scope: change it in this task (no CR needed).");
                return 1;
            }
            bool core = to == CrLog.CoreTarget;
            string apiFile = Path.Combine(root, CrStore.ContractsDir(module) + "PublicApi.txt");
            var (kind, why) = CrClassifier.Classify(File.Exists(apiFile) ? File.ReadAllText(apiFile) : "", api, core);
            if (why != null)
            {
                Console.Error.WriteLine("Cannot file: " + why);
                return 64;
            }
            if (patch != null && !File.Exists(Path.Combine(root, patch)))
            {
                Console.Error.WriteLine("Cannot file: patch " + patch + " not found (put it in .scratch/)");
                return 1;
            }
            string id = CrStore.NextId(root);
            string session = TestSeal.CurrentSession(root);
            string line = "file " + id + " from=" + from + " to=" + to + " module=" + module + " kind=" + kind + " task=" + task.Id + " session=" + (session ?? "unknown") +
                          " ts=" + Now + (api.Count > 0 ? " api=" + CrLog.EncodeApi(api) : "") +
                          (patch != null ? " patch=" + patch.Replace('\\', '/') + "@" + WorkSnapshot.HashFile(Path.Combine(root, patch)) : "") + " reason=" + text.Trim();
            CrStore.Append(root, to, line);
            Recorder.Emit(Recorder.New(EventTypes.CrFiled, "cr-cli").With("id", id).With("from", from).With("to", to).With("kind", kind).With("text", AgentHooks.Trim(text, 200)));
            string route = CrClassifier.Route(kind);
            Console.WriteLine(id + " filed: " + from + " → " + to + " (" + kind + ")");
            if (route == CrClassifier.Gate)
            {
                CrStore.Append(root, to, "approve " + id + " by=gate ts=" + Now + " reason=additive: only new types in " + module + "/Contracts");
                task.CrGrants[id] = module;
                TaskStore.Save(root, task);
                Console.WriteLine("Approved by the gate (only new types). This task may now write " + CrStore.ContractsDir(module) +
                                  ": the new type as a skeleton + exactly these lines in its PublicApi.txt. " + module + " implements it later (" + id + " is in its lane).");
            }
            else if (route == CrClassifier.ArbiterRoute)
                Console.WriteLine("Waits for an arbiter (another session): ./Tools/test-gate.sh cr rule " + id + " keep|reject|ask \"<why>\"");
            else
                Console.WriteLine("Waits for the owner (batched): they run ./Tools/test-gate.sh cr decide in a terminal.");
            Console.WriteLine("Don't wait: keep working against a fake in your own Tests/ or DevTools/. Your feature owes " + id + " until " + to + " delivers it.");
            return 0;
        }

        public static int Cli(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "list";
            string Arg(int i) => args.Length > i ? args[i] : null;
            var crs = CrStore.Load(root);
            switch (sub)
            {
                case "list":
                    bool all = args.Contains("--all");
                    foreach (var c in crs.Values.Where(c => all || !c.Closed).OrderBy(c => Num(c.Id)))
                        Console.WriteLine(c.Id.PadRight(7) + c.Status.PadRight(10) + c.Kind.PadRight(14) + (c.From + " → " + c.To).PadRight(30) +
                                          (c.Status == CrLog.Open || c.Status == CrLog.Asked ? "[" + WhoDecides(c) + "] " : "") + c.Text);
                    if (crs.Count == 0)
                        Console.WriteLine("No change requests.");
                    return 0;
                case "show":
                    if (Arg(2) == null || !crs.TryGetValue(Arg(2), out var cr))
                        return Fail("Usage: cr show CR-n (unknown '" + Arg(2) + "')", 64);
                    Console.WriteLine(cr.Id + "  " + cr.Status + "  " + cr.Kind + "  " + cr.From + " → " + cr.To + " (module " + cr.Module + ")  decides: " + WhoDecides(cr));
                    Console.WriteLine("  " + cr.Text);
                    foreach (var a in cr.Api)
                        Console.WriteLine("  api: " + a);
                    if (cr.Patch != null)
                        Console.WriteLine("  patch (proposed by the asker, applied only by " + cr.To + "'s sessions): " + cr.Patch);
                    foreach (var h in cr.History)
                        Console.WriteLine("  | " + h);
                    return 0;
                case "rule":
                    return Rule(root, crs, Arg(2), Arg(3), Arg(4));
                case "withdraw":
                    return Withdraw(root, crs, Arg(2), Arg(3));
                case "deliver":
                    return Deliver(root, crs, Arg(2), Arg(3));
                case "decide":
                    return Decide(root, crs);
                default:
                    return Fail("usage: cr list [--all] | show CR-n | rule CR-n keep|reject|ask \"why\" | withdraw CR-n \"why\" | deliver CR-n \"what\" | (owner) decide", 2);
            }
        }

        public static string WhoDecides(CrEntry c) => c.Status == CrLog.Asked ? CrClassifier.Owner : CrClassifier.Route(c.Kind);

        private static int Rule(string root, Dictionary<string, CrEntry> crs, string id, string ruling, string why)
        {
            if (id == null || !crs.TryGetValue(id, out var cr) || !new[] { "keep", "reject", "ask" }.Contains(ruling) || string.IsNullOrWhiteSpace(why))
                return Fail("Usage: cr rule CR-n keep|reject|ask \"<why>\"", 64);
            if (cr.Status != CrLog.Open || WhoDecides(cr) != CrClassifier.ArbiterRoute)
                return Fail(id + " is " + cr.Status + " and decided by " + WhoDecides(cr) + ": not an arbiter's call", 1);
            string session = TestSeal.CurrentSession(root);
            if (session == null || session == cr.Session)
                return Fail("Refused: the session that asked for " + id + " can't rule on it (another session is the arbiter)", 3);
            string verb = ruling == "keep" ? "approve" : ruling;
            CrStore.Append(root, cr.To, verb + " " + id + " by=arbiter:" + session + " ts=" + Now + " reason=" + why.Trim());
            Console.WriteLine(id + ": " + (ruling == "keep" ? "approved: it is now a requirement of " + cr.To + " (roadmap next)" : ruling == "reject" ? "rejected (the asker changes approach or files another CR)" : "asked the owner (cr decide)"));
            return 0;
        }

        private static int Withdraw(string root, Dictionary<string, CrEntry> crs, string id, string why)
        {
            if (id == null || !crs.TryGetValue(id, out var cr) || string.IsNullOrWhiteSpace(why))
                return Fail("Usage: cr withdraw CR-n \"<why>\"", 64);
            if (cr.IsRequirement)
                return Fail(id + " is approved: it is a requirement of " + cr.To + " now (the owner removes it, or B delivers it)", 1);
            if (cr.Closed)
                return Fail(id + " is already " + cr.Status, 1);
            CrStore.Append(root, cr.To, "withdraw " + id + " by=" + (TestSeal.CurrentSession(root) ?? "unknown") + " ts=" + Now + " reason=" + why.Trim());
            Console.WriteLine(id + " withdrawn.");
            return 0;
        }

        private static int Deliver(string root, Dictionary<string, CrEntry> crs, string id, string what)
        {
            if (id == null || !crs.TryGetValue(id, out var cr) || string.IsNullOrWhiteSpace(what))
                return Fail("Usage: cr deliver CR-n \"<what was done>\"", 64);
            if (cr.To != CrLog.CoreTarget || cr.Status != CrLog.Approved)
                return Fail(id + ": only an approved Core CR is delivered by hand (feature CRs are delivered by their tests: VERIFIED)", 1);
            var task = TaskStore.Load(root);
            if (task == null || !task.IsActive || !task.CoreMaintenance)
                return Fail("Core CRs are done in a core-maintenance task of their own (task request --core, the owner approves)", 1);
            CrStore.Append(root, cr.To, "deliver " + id + " by=" + task.Id + " ts=" + Now + " reason=" + what.Trim());
            Console.WriteLine(id + " delivered.");
            return 0;
        }

        /// <summary>The owner's pass over everything waiting for them: y = approve, n = reject, Enter = later.</summary>
        private static int Decide(string root, Dictionary<string, CrEntry> crs)
        {
            if (!AgentShell.IsHumanTerminal)
            {
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "cr-cli").With("target", "cr decide").With("reason", "not an interactive human terminal"));
                return Fail("Refused: cr decide is the owner's (needs a terminal, not an agent shell).", 3);
            }
            var waiting = crs.Values.Where(c => (c.Status == CrLog.Open || c.Status == CrLog.Asked) && WhoDecides(c) == CrClassifier.Owner).OrderBy(c => Num(c.Id)).ToList();
            if (waiting.Count == 0)
            {
                Console.WriteLine("Nothing waits for you.");
                return 0;
            }
            Recorder.Session = "human";
            foreach (var c in waiting)
            {
                Console.WriteLine();
                Console.WriteLine(c.Id + "  " + c.Kind + "  " + c.From + " → " + c.To + (c.Reason != null ? "  (arbiter: " + c.Reason + ")" : ""));
                Console.WriteLine("  " + c.Text);
                foreach (var a in c.Api)
                    Console.WriteLine("  api: " + a);
                Console.Write("Approve? [y]es / [n]o / Enter = later: ");
                string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (answer != "y" && answer != "n")
                    continue;
                Console.Write("Why (optional): ");
                string why = (Console.ReadLine() ?? "").Trim();
                CrStore.Append(root, c.To, (answer == "y" ? "approve " : "reject ") + c.Id + " by=owner ts=" + Now + " reason=" + (why.Length > 0 ? why : "owner"));
                if (answer == "y" && c.Kind.EndsWith(CrClassifier.Meaning, StringComparison.Ordinal) && c.To != CrLog.CoreTarget)
                    Console.WriteLine("  → " + c.Id + " is now a requirement of " + c.To + ". To reword the plan itself, add the R- line to features.md.");
            }
            return 0;
        }

        /// <summary>Target → (plan feature or CORE, code module, error).</summary>
        public static (string to, string module, string error) Resolve(Plan plan, string target)
        {
            if (string.Equals(target, "Core", StringComparison.OrdinalIgnoreCase) || target == CrLog.CoreTarget)
                return (CrLog.CoreTarget, "Core", null);
            if (plan == null)
                return (null, null, "no plan (Docs/Plan/features.md): CRs target plan features");
            var f = plan.Feature(target);
            if (f != null)
                return f.Modules.Count == 1 ? (f.Id, f.Modules[0], null)
                    : (null, null, f.Id + " lives in " + string.Join(", ", f.Modules) + ": name the module instead (task need <Module> …)");
            var owners = plan.Features.Where(x => x.Active && x.Modules.Contains(target)).ToList();
            if (owners.Count == 0)
                return (null, null, "no active plan feature lists module '" + target + "' (unknown target: propose it with roadmap propose)");
            // A module shared by several features: the CR goes to the one that owns its contracts (highest priority, then id).
            var owner = owners.OrderBy(x => x.PriorityRank).ThenBy(x => x.Id, StringComparer.Ordinal).First();
            return (owner.Id, target, null);
        }

        public static string FromOf(Plan plan, AgentTask task)
        {
            var req = task.Requirements.Keys.Select(k => plan?.Requirement(k)).FirstOrDefault(r => r != null);
            if (req != null)
                return req.Feature;
            string module = task.Features.FirstOrDefault();
            var f = plan?.Features.Where(x => x.Active && x.Modules.Contains(module)).OrderBy(x => x.PriorityRank).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
            return f?.Id ?? module ?? "core";
        }

        private static int Num(string id) => int.TryParse(id.Substring(3), out int n) ? n : 0;

        private static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }
    }
}
