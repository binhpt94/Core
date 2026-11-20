using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    public sealed class TestRun
    {
        public string Key { get; set; }        // ShortClass.Method
        public bool Passed { get; set; }
        public string Message { get; set; }
    }

    public sealed class SealOutcome
    {
        public bool Ok { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
        public static SealOutcome Fail(string m) => new SealOutcome { Ok = false, Messages = { m } };
    }

    /// <summary>
    /// `test-gate.sh tests …`: seal requirement tests (lint + red first), dispute them (impl), rule on disputes (arbiter),
    /// and the owner's last resorts (unseal, grandfather). See SealStore for the log and TestRoles for who writes what.
    /// </summary>
    public static class TestSeal
    {
        public const string SessionFile = "session-current";
        /// <summary>Runs the covering tests: (root, dotnet filter) → results, or null when they don't build. Replaceable in tests.</summary>
        public static Func<string, string, List<TestRun>> Runner = DotnetRunner;
        private static readonly Regex ExceptionMessage = new Regex(@"^\s*[\w\.`+]+(Exception|Error)\s*:");

        /// <summary>The session of the agent's latest tool call (written by the PreToolUse hook into .agent/, which agents can't write).</summary>
        public static string CurrentSession(string root)
        {
            string p = Path.Combine(root, ".agent", SessionFile);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }

        /// <summary>
        /// Why this session may not work this impl/arbiter task (null = it may): an impl session must not be the author of the tests it
        /// implements; an arbiter must be neither their author nor the session that disputed them.
        /// </summary>
        public static string SessionConflict(string root, AgentTask task, string session)
        {
            if (string.IsNullOrEmpty(session) || task == null)
                return null;
            var seals = SealStore.Load(root);
            foreach (var id in task.Requirements.Keys)
            {
                if (!seals.TryGetValue(id, out var s) || s.Void)
                    continue;
                if (session == s.Author)
                    return "this session wrote and sealed the tests of " + id + ": " + (task.Role == TestRoles.Impl ? "its code" : "a ruling on them") +
                           " must come from another session (separation of duties). Start a new session for this task.";
                if (task.Role == TestRoles.Arbiter && session == s.DisputedBy)
                    return "this session disputed " + id + ": the ruling must come from a third session. Start a new session for this task.";
            }
            return null;
        }

        public const string NotCritical = "not [critical]: only [critical] requirements are sealed (separation of duties); " +
                                          "the others count when their tests pass, written and implemented in one task without --role";

        /// <summary>Seals, disputes and rulings matter only for [critical] requirements (a requirement no longer in the plan: no).</summary>
        public static bool IsCritical(Plan plan, string id) => plan?.Requirement(id)?.IsCritical == true;

        // ------------------------------------------------------------------ seal

        public static SealOutcome Seal(string root, AgentTask task, IEnumerable<string> reqs, string session, string now)
        {
            if (task == null || !task.IsActive || (task.Role != TestRoles.Tests && task.Role != TestRoles.Arbiter))
                return SealOutcome.Fail("seal runs in a task with --role tests (or arbiter after a \"fix\" ruling)");
            var plan = PlanParser.Load(root);
            var covers = PlanRules.Covers(root);
            var seals = SealStore.Load(root);
            var outcome = new SealOutcome { Ok = true };
            foreach (var id in reqs)
            {
                var r = plan?.Requirement(id);
                string fail = r == null ? id + ": not in the plan"
                    : !task.Requirements.ContainsKey(id) ? id + ": not a requirement of task " + task.Id
                    : r.IsManual ? id + ": [manual] requirements are accepted by a human, not sealed"
                    : !r.IsCritical ? id + ": " + NotCritical
                    : null;
                var mine = covers.Where(c => c.Requirement == id).ToList();
                if (fail == null && mine.Count == 0)
                    fail = id + ": no test has [Covers(\"" + id + "\")]";
                seals.TryGetValue(id, out var state);
                if (fail == null && state != null && !state.Void && state.RequirementHash == r.Hash && state.Hash == SealStore.TestsHash(root, mine))
                    fail = id + ": already sealed by " + state.Author + " (a wrong test is disputed, then fixed by an arbiter)";
                fail = fail ?? Dirty(root, task, mine) ?? AuthorProblem(root, task, mine, session) ?? Lint(root, mine);
                RedResult red = null;
                if (fail == null)
                {
                    red = Red(root, mine, state == null ? null : state.Void ? state.PreviousCommit : state.Commit);
                    if (red.Verdict != "red" && red.Verdict != "pending")
                        fail = id + ": " + red.Detail;
                }
                if (fail != null)
                {
                    outcome.Ok = false;
                    outcome.Messages.Add(fail);
                    continue;
                }
                string head = AgentGit.Run(root, "rev-parse HEAD").output.Trim();
                SealStore.Append(root, r.Feature, "seal " + id + " tests=" + SealStore.TestsHash(root, mine) + " req=" + r.Hash + " author=" + session +
                                                  " red=" + (red.Verdict == "pending" ? "pending" : red.Failed + "/" + red.Total) + " commit=" + head + " ts=" + now);
                outcome.Messages.Add(id + ": sealed (" + red.Detail + ")");
            }
            return outcome;
        }

        /// <summary>Tests + skeleton must be committed: the seal names the commit they were red against.</summary>
        private static string Dirty(string root, AgentTask task, List<CoverEntry> covers)
        {
            var paths = covers.Select(c => c.File).Distinct().Concat(task.Features.SelectMany(f => new[]
                { "UnityProject/Assets/_Game/Features/" + f + "/Logic", "UnityProject/Assets/_Game/Features/" + f + "/Contracts" }));
            string status = AgentGit.Run(root, "status --porcelain -- " + string.Join(" ", paths.Select(p => "\"" + p + "\""))).output.Trim();
            return status.Length == 0 ? null : "commit the tests and the skeleton first (a seal names the commit they were red against):\n" + status;
        }

        /// <summary>The covering tests were written in this task by exactly one session, and it is the one sealing.</summary>
        private static string AuthorProblem(string root, AgentTask task, List<CoverEntry> covers, string session)
        {
            if (string.IsNullOrEmpty(session))
                return "unknown session (run this from the agent session that wrote the tests)";
            var events = EventLog.For(root).ReadAll();
            int start = events.FindLastIndex(e => e.Type == EventTypes.TaskCreated && e.Get("id") == task.Id);
            var files = new HashSet<string>(covers.Select(c => c.File), StringComparer.Ordinal);
            var writers = events.Skip(Math.Max(0, start)).Where(e => e.Type == EventTypes.FileModified && files.Contains(e.Get("path") ?? ""))
                .Select(e => e.Session).Where(s => !string.IsNullOrEmpty(s) && s != "cli").Distinct().ToList();
            if (writers.Count == 0)
                return "these tests were not written in task " + task.Id + " (write or update them here, then seal)";
            if (writers.Count > 1 || writers[0] != session)
                return "the tests were written by session(s) " + string.Join(", ", writers) + ", not only by this one (" + session + ")";
            return null;
        }

        private static string Lint(string root, List<CoverEntry> covers)
        {
            var problems = new List<string>();
            foreach (var c in covers)
            {
                string full = Path.Combine(root, c.File);
                string file = File.Exists(full) ? SourceText.StripComments(File.ReadAllText(full)) : null;
                problems.AddRange(TestLint.Check(SealStore.MethodText(root, c), file).Select(p => c.TestKey + ": " + p));
            }
            return problems.Count == 0 ? null : "test lint:\n  " + string.Join("\n  ", problems);
        }

        // ------------------------------------------------------------------ red first

        public sealed class RedResult
        {
            public string Verdict { get; set; }   // red | green | flaky | exceptions | pending | build
            public int Failed { get; set; }
            public int Total { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>Runs the covering tests twice, against the current code or, for a re-seal, against the code of the old seal.</summary>
        public static RedResult Red(string root, List<CoverEntry> covers, string baseCommit)
        {
            string filter = Filter(covers);
            if (baseCommit == null)
                return EvaluateRed(covers, Runner(root, filter), Runner(root, filter));
            string tmp = Path.Combine(Path.GetTempPath(), "seal-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                if (AgentGit.Run(root, "worktree add --detach \"" + tmp + "\" " + baseCommit).code != 0)
                    return new RedResult { Verdict = "build", Detail = "cannot check out the old seal's commit " + baseCommit };
                foreach (var f in covers.Select(c => c.File).Distinct())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(tmp, f)));
                    File.Copy(Path.Combine(root, f), Path.Combine(tmp, f), overwrite: true);
                }
                var result = EvaluateRed(covers, Runner(tmp, filter), Runner(tmp, filter));
                result.Detail += " (against the code of the previous seal, " + baseCommit.Substring(0, Math.Min(8, baseCommit.Length)) + ")";
                return result;
            }
            finally
            {
                AgentGit.Run(root, "worktree remove --force \"" + tmp + "\"");
            }
        }

        public static string Filter(IEnumerable<CoverEntry> covers) =>
            string.Join("|", covers.Select(c => "FullyQualifiedName~." + c.Class + "." + (c.Method ?? "")).Distinct());

        /// <summary>
        /// red = builds, both runs agree, and at least one covering test fails on an ASSERTION. A test that only throws (e.g. a missing
        /// implementation) is not "red": it doesn't prove the assertion checks anything.
        /// </summary>
        public static RedResult EvaluateRed(IList<CoverEntry> covers, List<TestRun> first, List<TestRun> second)
        {
            if (first == null || second == null)
                return new RedResult { Verdict = "build", Detail = "the tests do not build (a skeleton with default bodies keeps them compiling)" };
            var a = Matching(covers, first);
            var b = Matching(covers, second);
            if (a.Count == 0)
                return new RedResult { Verdict = "pending", Detail = "no covering test ran in dotnet (Unity-only): red first is confirmed by CI/Unity" };
            string Sig(List<TestRun> runs) => string.Join(",", runs.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Passed));
            if (Sig(a) != Sig(b))
                return new RedResult { Verdict = "flaky", Total = a.Count, Detail = "two runs gave different results: flaky tests can't be sealed" };
            int assertFails = a.Count(x => !x.Passed && !ExceptionMessage.IsMatch(x.Message ?? ""));
            if (a.All(x => x.Passed))
                return new RedResult { Verdict = "green", Total = a.Count, Detail = "every covering test already passes: it checks nothing the code doesn't already do" };
            if (assertFails == 0)
                return new RedResult { Verdict = "exceptions", Total = a.Count, Detail = "tests fail only by exceptions, not on an assertion (use a skeleton with default bodies, not throw)" };
            return new RedResult { Verdict = "red", Failed = assertFails, Total = a.Count, Detail = "red first: " + assertFails + "/" + a.Count + " fail on an assertion, stable over 2 runs" };
        }

        private static List<TestRun> Matching(IEnumerable<CoverEntry> covers, List<TestRun> runs) =>
            runs.Where(t => covers.Any(c => c.Method == null ? t.Key.StartsWith(c.Class + ".", StringComparison.Ordinal) : t.Key == c.Class + "." + c.Method)).ToList();

        private static List<TestRun> DotnetRunner(string root, string filter)
        {
            string dir = Path.Combine(Path.GetTempPath(), "sealrun-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var psi = new ProcessStartInfo("dotnet", "test \"" + Path.Combine(root, "Tools", "LogicTests") + "\" --filter \"" + filter + "\" --logger \"trx;LogFileName=seal.trx\" --results-directory \"" + dir + "\" --nologo -v q")
            { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            string trx = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (trx == null)
                return null;
            var doc = XDocument.Load(trx);
            XNamespace ns = doc.Root.Name.Namespace;
            var classes = doc.Descendants(ns + "UnitTest").ToDictionary(u => (string)u.Attribute("id"), u => (string)u.Element(ns + "TestMethod")?.Attribute("className") ?? "");
            var runs = doc.Descendants(ns + "UnitTestResult").Select(r =>
            {
                string cls = classes.TryGetValue((string)r.Attribute("testId") ?? "", out var c) ? c : "";
                string shortCls = cls.Substring(cls.LastIndexOf('.') + 1);
                string name = ((string)r.Attribute("testName") ?? "").Split('(')[0];
                name = name.Substring(name.LastIndexOf('.') + 1);
                return new TestRun { Key = shortCls + "." + name, Passed = (string)r.Attribute("outcome") == "Passed", Message = r.Descendants(ns + "Message").FirstOrDefault()?.Value };
            }).ToList();
            try { Directory.Delete(dir, true); } catch (IOException) { }
            return runs;
        }

        // ------------------------------------------------------------------ the gate: red by design vs regression

        /// <summary>Test keys (ShortClass.Method) of live seals whose requirement never passed yet: their red is pending work, not a failure.</summary>
        public static HashSet<string> AwaitingImpl(string root)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var seals = SealStore.Load(root);
            var plan = PlanParser.Load(root);
            foreach (var c in PlanRules.Covers(root))
                if (IsCritical(plan, c.Requirement) && seals.TryGetValue(c.Requirement, out var s) && !s.Void && !s.Verified && s.Red != "legacy")
                    keys.Add(c.TestKey);
            return keys;
        }

        public static string KeyOf(string testName, string className)
        {
            string name = (testName ?? "").Split('(')[0];
            name = name.Substring(name.LastIndexOf('.') + 1);
            return className.Substring(className.LastIndexOf('.') + 1) + "." + name;
        }

        /// <summary>After a green gate: live seals whose covering tests all passed are recorded "verified" in the (tracked) seal log.</summary>
        public static List<string> MarkVerified(string root, Plan plan, TestGate.Planning.TestResultsIndex results)
        {
            var done = new List<string>();
            var covers = PlanRules.Covers(root);
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            foreach (var s in SealStore.Load(root).Values.Where(x => !x.Void && !x.Verified && x.Red != "legacy" && IsCritical(plan, x.Requirement)))
            {
                var mine = covers.Where(c => c.Requirement == s.Requirement).ToList();
                if (mine.Count == 0 || mine.Any(c => results.Find(c.TestKey).Count == 0) || mine.SelectMany(c => results.Find(c.TestKey)).Any(o => !o.Passed))
                    continue;
                if (SealStore.TestsHash(root, mine) != s.Hash)
                    continue;   // tampered: not verified
                SealStore.Append(root, s.Feature, "verified " + s.Requirement + " ts=" + now);
                done.Add(s.Requirement);
            }
            return done;
        }

        // ------------------------------------------------------------------ disputes, rulings, owner's last resorts

        public static SealOutcome Dispute(string root, AgentTask task, string id, string session, string reason, string now)
        {
            if (task == null || !task.IsActive || task.Role != TestRoles.Impl)
                return SealOutcome.Fail("dispute runs in a task with --role impl");
            if (!task.Requirements.ContainsKey(id))
                return SealOutcome.Fail(id + ": not a requirement of task " + task.Id);
            if (!IsCritical(PlanParser.Load(root), id))
                return SealOutcome.Fail(id + ": " + NotCritical);
            if (string.IsNullOrWhiteSpace(reason))
                return SealOutcome.Fail("say why the test is wrong, with evidence (what the requirement says vs what the test demands)");
            var seals = SealStore.Load(root);
            if (!seals.TryGetValue(id, out var s) || s.Void)
                return SealOutcome.Fail(id + ": not sealed, nothing to dispute");
            if (s.OpenDispute)
                return SealOutcome.Fail(id + ": already disputed, waiting for an arbiter (work on another requirement)");
            SealStore.Append(root, s.Feature, "dispute " + id + " by=" + session + " ts=" + now + " reason=" + reason.Trim());
            var outcome = new SealOutcome { Ok = true, Messages = { id + ": disputed. An arbiter session settles it (lane \"Phân xử\"); work on another requirement meanwhile." } };
            if (s.DisputeCount + 1 >= SealStore.AskAfterDisputes)
            {
                SealStore.Append(root, s.Feature, "rule " + id + " ask by=gate ts=" + now + " reason=disputed " + (s.DisputeCount + 1) + " times: the owner decides the wording");
                outcome.Messages.Add(id + ": disputed " + (s.DisputeCount + 1) + " times, so it goes to the owner (reword the requirement)");
            }
            return outcome;
        }

        public static SealOutcome Rule(string root, AgentTask task, string id, string ruling, string session, string reason, string now)
        {
            if (task == null || !task.IsActive || task.Role != TestRoles.Arbiter)
                return SealOutcome.Fail("rule runs in a task with --role arbiter");
            if (!task.Requirements.ContainsKey(id))
                return SealOutcome.Fail(id + ": not a requirement of task " + task.Id);
            if (!IsCritical(PlanParser.Load(root), id))
                return SealOutcome.Fail(id + ": " + NotCritical);
            if (ruling != "keep" && ruling != "fix" && ruling != "ask")
                return SealOutcome.Fail("ruling: keep (the test is right) | fix (the test is wrong: fix it and seal again) | ask (the requirement is ambiguous)");
            if (string.IsNullOrWhiteSpace(reason))
                return SealOutcome.Fail("give the reason for the ruling (it is shown to the impl session and on the dashboard)");
            var seals = SealStore.Load(root);
            if (!seals.TryGetValue(id, out var s) || s.Void || !(s.OpenDispute || s.Suspect))
                return SealOutcome.Fail(id + ": nothing to rule on (no open dispute, not marked for re-check)");
            if (session == s.Author || session == s.DisputedBy)
                return SealOutcome.Fail("an arbiter must be another session than the one that wrote the tests (" + s.Author + ") and the one that disputed them (" + s.DisputedBy + ")");
            SealStore.Append(root, s.Feature, "rule " + id + " " + ruling + " by=" + session + " ts=" + now + " reason=" + reason.Trim());
            return new SealOutcome
            {
                Ok = true,
                Messages =
                {
                    ruling == "keep" ? id + ": the test stands; the impl session reads the reason and continues"
                    : ruling == "fix" ? id + ": unsealed. Fix the test in this task, commit, then ./Tools/test-gate.sh tests seal " + id + " (red against the old seal's code)"
                    : id + ": the owner is asked: propose the new wording (the features.md yes/no in chat)"
                }
            };
        }

        public static SealOutcome Unseal(string root, string id, string reason, string now)
        {
            var seals = SealStore.Load(root);
            if (!seals.TryGetValue(id, out var s) || s.Void)
                return SealOutcome.Fail(id + ": not sealed");
            SealStore.Append(root, s.Feature, "unseal " + id + " by=owner ts=" + now + " reason=" + (reason ?? "").Trim());
            return new SealOutcome { Ok = true, Messages = { id + ": unsealed by the owner" } };
        }

        /// <summary>Once, by the owner: today's covering tests count as sealed (author=legacy), so verified requirements don't all drop.</summary>
        /// <summary>What `tests unseal` would do (the owner's yes/no dialog).</summary>
        public static (string text, string error) DescribeUnseal(string root, string id, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (null, "usage: tests unseal R-ID \"<why>\" (the why is required)");
            if (id == null || !SealStore.Load(root).TryGetValue(id, out var s) || s.Void)
                return (null, id + ": not sealed");
            return ("Unseal " + id + " (author=" + s.Author + ", red=" + s.Red + (s.DisputeCount > 0 ? ", disputes=" + s.DisputeCount : "") + "): its tests stop counting until sealed again.\n  why: " + reason.Trim(), null);
        }

        /// <summary>What `tests grandfather` would seal (the owner's yes/no dialog).</summary>
        public static (string text, string error) DescribeGrandfather(string root)
        {
            var o = Grandfather(root, null, write: false);
            if (!o.Ok)
                return (null, string.Join("; ", o.Messages));
            if (o.Messages.Count == 0)
                return (null, "nothing to grandfather (every covered requirement is sealed)");
            return ("Grandfather " + o.Messages.Count + " requirement(s): today's covering tests count as sealed (author=legacy):\n  " +
                    string.Join("\n  ", o.Messages.Take(30)) + (o.Messages.Count > 30 ? "\n  … +" + (o.Messages.Count - 30) : ""), null);
        }

        public static SealOutcome Grandfather(string root, string now, bool write = true)
        {
            var plan = PlanParser.Load(root);
            if (plan == null)
                return SealOutcome.Fail("no plan");
            var covers = PlanRules.Covers(root);
            var seals = SealStore.Load(root);
            string head = write ? AgentGit.Run(root, "rev-parse HEAD").output.Trim() : null;
            var outcome = new SealOutcome { Ok = true };
            foreach (var r in plan.AllRequirements.Where(x => !x.IsManual))
            {
                var mine = covers.Where(c => c.Requirement == r.Id).ToList();
                if (mine.Count == 0 || seals.TryGetValue(r.Id, out var s) && !s.Void)
                    continue;
                if (!write)
                {
                    outcome.Messages.Add(r.Id + " (" + mine.Count + " test(s))");
                    continue;
                }
                SealStore.Append(root, r.Feature, "seal " + r.Id + " tests=" + SealStore.TestsHash(root, mine) + " req=" + r.Hash + " author=legacy red=legacy commit=" + head + " ts=" + now);
                outcome.Messages.Add(r.Id + ": grandfathered");
            }
            return outcome;
        }

        // ------------------------------------------------------------------ CLI

        public static int Cli(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "status";
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var task = TaskStore.Load(root);
            string session = CurrentSession(root);
            string Arg(int i) => args.Length > i ? args[i] : null;
            SealOutcome o;
            switch (sub)
            {
                case "seal": o = Seal(root, task, args.Skip(2).Where(a => a.StartsWith("R-", StringComparison.Ordinal) || a.StartsWith("CR-", StringComparison.Ordinal)), session, now); break;
                case "dispute": o = Dispute(root, task, Arg(2), session, Arg(3), now); break;
                case "rule": o = Rule(root, task, Arg(2), Arg(3), session, Arg(4), now); break;
                case "unseal":
                case "grandfather":
                    if (OwnerAsk.Authorize(root, args, "tests-cli", "tests " + sub + " is the owner's last resort.") == null)
                        return 3;
                    o = sub == "unseal" ? Unseal(root, Arg(2), Arg(3), now) : Grandfather(root, now);
                    break;
                case "summary":
                    return TestSummary.Cli(root, Arg(2));
                case "status":
                    foreach (var s in SealStore.Load(root).Values.OrderBy(x => x.Feature).ThenBy(x => x.Requirement))
                        Console.WriteLine(s.Requirement.PadRight(22) + (s.Void ? "unsealed" : s.Ask ? "ask owner" : s.OpenDispute ? "disputed" : s.Suspect ? "re-check" : "sealed") +
                                          "  author=" + s.Author + "  red=" + s.Red + (s.DisputeCount > 0 ? "  disputes=" + s.DisputeCount : ""));
                    return 0;
                default:
                    Console.Error.WriteLine("usage: tests summary [R-ID|F-ID] | ([critical] only) seal R-ID… | dispute R-ID \"why\" | rule R-ID keep|fix|ask \"why\" | status | (owner) unseal R-ID \"why\" | grandfather");
                    return 2;
            }
            foreach (var m in o.Messages)
                (o.Ok ? Console.Out : Console.Error).WriteLine(m);
            return o.Ok ? 0 : 1;
        }
    }
}
