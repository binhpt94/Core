using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TestGate.AgentControl;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>Requirement statuses. COMPUTED, never stored or set by an agent.</summary>
    public static class ReqStatus
    {
        public const string Verified = "VERIFIED";          // every covering test passed, after the last relevant code change
        public const string Accepted = "ACCEPTED";          // [manual], accepted by a human for this exact wording
        public const string NoTest = "NO_TEST";             // nothing covers it
        public const string NotRun = "NOT_RUN";             // covering tests have no result yet
        public const string Failing = "FAILING";
        public const string Unverified = "UNVERIFIED";      // code of its modules changed after the last test run
        public const string Stale = "STALE";                // its wording changed after it was verified
        public const string NeedsReview = "NEEDS_REVIEW";   // [manual], waiting for a human
        public const string Unsealed = "UNSEALED";          // [critical]: tests exist but no independent session sealed them (TestRoles)
        public const string Tampered = "TAMPERED";          // sealed tests changed after sealing
        public const string Disputed = "DISPUTED";          // the impl session says a sealed test is wrong: an arbiter (or the owner) decides
        public const string Deferred = "DEFERRED";
        public const string Dropped = "DROPPED";

        public static bool IsDone(string s) => s == Verified || s == Accepted;

        public static int Severity(string s)
        {
            switch (s)
            {
                case Failing: return 0;
                case Stale: return 1;
                case Tampered: return 1;
                case Unverified: return 2;
                case Disputed: return 2;
                case NotRun: return 3;
                case NoTest: return 4;
                case Unsealed: return 4;
                default: return 5;
            }
        }
    }

    public static class FeatureStatus
    {
        public const string Done = "DONE";
        public const string InProgress = "IN_PROGRESS";
        public const string Planned = "PLANNED";
        public const string Blocked = "BLOCKED";
        public const string Deferred = "DEFERRED";
        public const string Dropped = "DROPPED";
    }

    public sealed class RequirementState
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public bool Manual { get; set; }
        public string Discipline { get; set; }
        public string Hash { get; set; }
        public List<string> Tests { get; set; } = new List<string>();
        public bool OwnerDecides { get; set; }           // a dispute escalated to the owner (reword the requirement)
        public string Role { get; set; }                 // who works this gap: tests | impl | arbiter (TestRoles; [critical] only), null = one task without --role
    }

    public sealed class DependencyState
    {
        public string Kind { get; set; }
        public string To { get; set; }
        public string Contract { get; set; }
        public bool Frozen { get; set; }
        public bool Satisfied { get; set; }
    }

    public sealed class FeatureState
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Priority { get; set; }
        public string Status { get; set; }
        public List<string> Modules { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public int Verified { get; set; }
        public int Total { get; set; }
        public List<string> Debt { get; set; } = new List<string>();
        /// <summary>Per branch: story goals and gameplay requirements verified/total; art = lowest level of its slots.</summary>
        public int StoryVerified { get; set; }
        public int StoryTotal { get; set; }
        public int GameplayVerified { get; set; }
        public int GameplayTotal { get; set; }
        public List<string> Slots { get; set; } = new List<string>();
        public string ArtRequired { get; set; }
        public string ArtLevel { get; set; }
        /// <summary>Art below the planned level or a failing art gate: work for the art lane; never blocks DONE, dependencies or task start.</summary>
        public List<string> ArtDebt { get; set; } = new List<string>();
        public bool ArtComplete => ArtDebt.Count == 0;
        /// <summary>[art-risk] feasibility requirements a human has not accepted yet: a warning for the owner, never a block.</summary>
        public List<string> ArtRisk { get; set; } = new List<string>();
        public List<string> BlockedBy { get; set; } = new List<string>();
        public List<RequirementState> Requirements { get; set; } = new List<RequirementState>();
        public List<DependencyState> Dependencies { get; set; } = new List<DependencyState>();
        public int Depth { get; set; }
        public string Progress => Verified + "/" + Total + " requirement đã kiểm chứng";
    }

    public sealed class Gap
    {
        public string Kind { get; set; }
        public string Feature { get; set; }
        public string Requirement { get; set; }
        public string Detail { get; set; }
        /// <summary>agent = work an agent can do now; human = needs a decision/acceptance.</summary>
        public string Actor { get; set; }
        public int Severity { get; set; }
        public string Role { get; set; }      // tests | impl | arbiter: which lane of `roadmap next` takes it

        public override string ToString() => Kind + " " + (Requirement ?? Feature) + ": " + Detail;
    }

    public sealed class PlanReport
    {
        public bool HasPlan { get; set; }
        public string Title { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<FeatureState> Features { get; set; } = new List<FeatureState>();
        public List<Gap> Gaps { get; set; } = new List<Gap>();
        public List<string> Proposals { get; set; } = new List<string>();
        public string ResultsUtc { get; set; }
        /// <summary>Change requests not closed yet (CrPlanning): open ones wait for a decision, approved ones are requirements.</summary>
        public List<CrView> Crs { get; set; } = new List<CrView>();

        public FeatureState Feature(string id) => Features.FirstOrDefault(f => f.Id == id);
        public RequirementState Requirement(string id) => Features.SelectMany(f => f.Requirements).FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Inputs the status needs, injectable for tests.</summary>
    public sealed class PlanInputs
    {
        public Plan Plan { get; set; }
        public List<CoverEntry> Covers { get; set; } = new List<CoverEntry>();
        public TestResultsIndex Results { get; set; } = new TestResultsIndex();
        public Dictionary<string, LedgerEntry> Ledger { get; set; } = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        /// <summary>Requirement id → hash accepted by a human (Docs/Plan/accepted.txt).</summary>
        public Dictionary<string, string> Accepted { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Module → newest change time of its code (UTC).</summary>
        public Func<string, DateTime> ModuleChangedUtc { get; set; } = _ => DateTime.MinValue;
        /// <summary>Interface name → frozen (listed in a PublicApi.txt baseline).</summary>
        public Func<string, bool> ContractFrozen { get; set; } = _ => false;
        public Func<string, string> TestsHash { get; set; } = _ => "";
        /// <summary>Sealed tests per requirement (SealStore) and the current hash of a requirement's covering test methods.</summary>
        public Dictionary<string, SealState> Seals { get; set; } = new Dictionary<string, SealState>(StringComparer.Ordinal);
        public Func<string, string> SealHash { get; set; } = _ => "";
        public List<string> Proposals { get; set; } = new List<string>();
        /// <summary>Computed art level of every slot (ArtStatus).</summary>
        public Dictionary<string, SlotState> Slots { get; set; } = new Dictionary<string, SlotState>(StringComparer.Ordinal);
        /// <summary>Feature folders with uncommitted changes (for UNPLANNED_CODE).</summary>
        public List<string> ChangedModules { get; set; } = new List<string>();
        /// <summary>Change requests between modules (Docs/Plan/CRs) and "now" for their age.</summary>
        public Dictionary<string, CrEntry> Crs { get; set; } = new Dictionary<string, CrEntry>(StringComparer.Ordinal);
        public DateTime Now { get; set; } = DateTime.UtcNow;

        public static PlanInputs Load(string root)
        {
            var plan = PlanParser.Load(root);
            var covers = PlanRules.Covers(root);
            var api = Directory.Exists(Path.Combine(root, "UnityProject", "Assets"))
                ? string.Join("\n", Directory.GetFiles(Path.Combine(root, "UnityProject", "Assets"), "PublicApi.txt", SearchOption.AllDirectories).Select(File.ReadAllText))
                : "";
            var moduleTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var crs = CrLog.Load(root);
            string inbox = Path.Combine(root, PlanParser.InboxFolder);
            var changed = TestGate.AgentControl.WorkSnapshot.Take(root).Keys
                .Select(p => Regex.Match(p, @"^Assets/_Game/Features/([^/]+)/")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
            return new PlanInputs
            {
                Plan = plan,
                Covers = covers,
                Results = TestResultsIndex.Load(root),
                Ledger = PlanLedger.Load(root),
                Accepted = LoadAccepted(root),
                ModuleChangedUtc = m =>
                {
                    if (moduleTimes.TryGetValue(m, out var t))
                        return t;
                    string dir = Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", m);
                    t = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max() : DateTime.MinValue;
                    return moduleTimes[m] = t;
                },
                ContractFrozen = iface => CrPlanning.Frozen(root, api, crs, iface),
                TestsHash = req => PlanLedger.TestsHash(root, covers.Where(c => c.Requirement == req)),
                Seals = SealStore.Load(root),
                SealHash = req => SealStore.TestsHash(root, covers.Where(c => c.Requirement == req)),
                Slots = ArtStatus.Compute(root),
                Proposals = Directory.Exists(inbox) ? Directory.GetFiles(inbox, "*.md").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>(),
                ChangedModules = changed,
                Crs = crs
            };
        }

        public static Dictionary<string, string> LoadAccepted(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string p = Path.Combine(root, PlanParser.AcceptedPath);
            if (File.Exists(p))
                foreach (var line in File.ReadAllLines(p))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].StartsWith("R-", StringComparison.Ordinal))
                        map[parts[0]] = parts[1];
                }
            return map;
        }
    }

    public static class PlanStatus
    {
        public static PlanReport Compute(PlanInputs input)
        {
            var report = new PlanReport();
            var plan = input.Plan;
            if (plan == null)
                return report;
            report.HasPlan = true;
            report.Title = plan.Title;
            report.Errors.AddRange(plan.Errors);
            report.Proposals.AddRange(input.Proposals);
            report.ResultsUtc = input.Results.LatestRunUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'");
            report.Crs = CrPlanning.Views(input.Crs, input.Now);

            foreach (var f in plan.Features)
            {
                var fs = new FeatureState
                {
                    Id = f.Id, Title = f.Title, Priority = f.Priority, Modules = f.Modules.ToList(),
                    Tags = f.Tags.OrderBy(t => t, StringComparer.Ordinal).ToList(), Depth = Depth(plan, f, 0)
                };
                var watched = f.Modules.Concat(f.Dependencies.Where(d => d.Kind != DependencyKind.Soft).SelectMany(d => plan.Feature(d.Feature)?.Modules ?? new List<string>())).Distinct().ToList();
                foreach (var r in f.Requirements)
                    fs.Requirements.Add(Requirement(input, f, r, watched));
                fs.Total = fs.Requirements.Count;
                fs.Verified = fs.Requirements.Count(r => ReqStatus.IsDone(r.Status));
                fs.StoryTotal = fs.Requirements.Count(r => r.Discipline == "story");
                fs.StoryVerified = fs.Requirements.Count(r => r.Discipline == "story" && ReqStatus.IsDone(r.Status));
                fs.GameplayTotal = fs.Requirements.Count(r => r.Discipline == "gameplay");
                fs.GameplayVerified = fs.Requirements.Count(r => r.Discipline == "gameplay" && ReqStatus.IsDone(r.Status));
                fs.Slots = f.Slots.ToList();
                fs.ArtRequired = f.ArtLevel;
                if (f.Slots.Count > 0)
                {
                    var states = f.Slots.Select(id => input.Slots.TryGetValue(id, out var st) ? st : new SlotState { Id = id, Level = ArtLevels.Graybox }).ToList();
                    fs.ArtLevel = states.OrderBy(x => ArtLevels.Rank(x.Level)).First().Level;
                    foreach (var st in states)
                    {
                        if (f.ArtLevel != null && ArtLevels.Rank(st.Level) < ArtLevels.RankOfPlan(f.ArtLevel))
                            fs.ArtDebt.Add(st.Id + ": " + st.Level + ", cần " + f.ArtLevel + " (" + st.Detail + ")");
                        else if (st.Gate == "failed")
                            fs.ArtDebt.Add(st.Id + ": art gate failed (" + string.Join("; ", st.Violations.Take(2)) + ")");
                    }
                }
                report.Features.Add(fs);
            }

            // Second pass: dependencies need every feature's own status first (topological by recursion with memo).
            var memo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fs in report.Features)
                Resolve(plan, report, fs, input, memo, new HashSet<string>(StringComparer.Ordinal));

            foreach (var fs in report.Features.Where(x => x.Status != FeatureStatus.Deferred && x.Status != FeatureStatus.Dropped))
            {
                foreach (var r in fs.Requirements.Where(x => !ReqStatus.IsDone(x.Status)))
                    report.Gaps.Add(new Gap
                    {
                        Kind = r.Status, Feature = fs.Id, Requirement = r.Id, Detail = r.Detail,
                        Actor = r.Status == ReqStatus.NeedsReview || r.OwnerDecides ? "human" : "agent", Severity = ReqStatus.Severity(r.Status), Role = r.Role
                    });
                foreach (var a in fs.ArtDebt)
                    report.Gaps.Add(new Gap { Kind = "ART_DEBT", Feature = fs.Id, Detail = a, Actor = "art", Severity = 5 });
                foreach (var a in fs.ArtRisk)
                    report.Gaps.Add(new Gap { Kind = "ART_RISK", Feature = fs.Id, Detail = a, Actor = "art", Severity = 4 });
                if (fs.Total == 0)
                    report.Gaps.Add(new Gap { Kind = "NO_REQUIREMENTS", Feature = fs.Id, Detail = "feature has no requirement yet", Actor = "human", Severity = 6 });
                foreach (var d in fs.Debt)
                    report.Gaps.Add(new Gap { Kind = "DEBT", Feature = fs.Id, Detail = d, Actor = "agent", Severity = 2 });
            }
            var planned = new HashSet<string>(plan.Features.Where(f => f.Active).SelectMany(f => f.Modules), StringComparer.Ordinal);
            foreach (var m in input.ChangedModules.Where(m => !planned.Contains(m)))
                report.Gaps.Add(new Gap { Kind = "UNPLANNED_CODE", Feature = null, Detail = "Features/" + m + " changed but no active feature lists it in [modules]", Actor = "human", Severity = 6 });
            report.Gaps.AddRange(CrPlanning.Gaps(input.Crs, input.Now));
            foreach (var p in input.Proposals)
                report.Gaps.Add(new Gap { Kind = "PROPOSAL", Detail = PlanParser.InboxFolder + p + " waits for the human", Actor = "human", Severity = 7 });

            report.Gaps = report.Gaps.OrderBy(g => g.Severity).ThenBy(g => PriorityOf(report, g.Feature)).ThenBy(g => g.Requirement ?? g.Feature, StringComparer.Ordinal).ToList();
            return report;
        }

        private static int PriorityOf(PlanReport r, string feature)
        {
            var f = feature == null ? null : r.Feature(feature);
            return f == null ? 9 : f.Priority.Length == 2 ? f.Priority[1] - '0' : 9;
        }

        private static RequirementState Requirement(PlanInputs input, PlanFeature f, PlanRequirement r, List<string> watched)
        {
            var s = new RequirementState { Id = r.Id, Text = r.Text, Manual = r.IsManual, Hash = r.Hash, Discipline = r.Discipline };
            var covers = input.Covers.Where(c => c.Requirement == r.Id).ToList();
            s.Tests = covers.Select(c => c.TestKey).Distinct().ToList();

            if (f.Dropped) { s.Status = ReqStatus.Dropped; s.Detail = "feature dropped"; return s; }
            if (f.Deferred) { s.Status = ReqStatus.Deferred; s.Detail = "feature deferred"; return s; }

            if (r.IsManual)
            {
                if (input.Accepted.TryGetValue(r.Id, out var h))
                {
                    s.Status = h == r.Hash ? ReqStatus.Accepted : ReqStatus.Stale;
                    s.Detail = h == r.Hash ? "accepted by a human" : "wording changed after acceptance: the human must accept again";
                }
                else
                {
                    s.Status = ReqStatus.NeedsReview;
                    s.Detail = "manual: a human checks it and runs `roadmap accept " + r.Id + "`";
                }
                return s;
            }

            if (covers.Count == 0)
            {
                s.Status = ReqStatus.NoTest;
                s.Role = r.IsCritical ? TestRoles.Tests : null;
                s.Detail = "no test has [Covers(\"" + r.Id + "\")]";
                return s;
            }
            // Separation of duties only for [critical]: other requirements count when their tests pass (one task, no --role).
            if (r.IsCritical && SealProblem(input, r, s))
                return s;
            s.Role = r.IsCritical ? TestRoles.Impl : null;
            var outcomes = covers.Select(c => (c, input.Results.Find(c.TestKey))).ToList();
            var missing = outcomes.Where(o => o.Item2.Count == 0).Select(o => o.c.TestKey).ToList();
            var failed = outcomes.SelectMany(o => o.Item2).Where(o => !o.Passed).Select(o => o.Key).Distinct().ToList();
            if (failed.Count > 0)
            {
                s.Status = ReqStatus.Failing;
                s.Detail = "failing: " + string.Join(", ", failed.Take(4));
                return s;
            }
            if (missing.Count > 0)
            {
                s.Status = ReqStatus.NotRun;
                s.Detail = "no result for " + string.Join(", ", missing.Take(4)) + " (run the gate)";
                return s;
            }
            if (input.Ledger.TryGetValue(r.Id, out var e) && e.RequirementHash != r.Hash)
            {
                s.Role = r.IsCritical ? TestRoles.Tests : null;
                s.Status = ReqStatus.Stale;
                s.Detail = e.TestsHash == input.TestsHash(r.Id)
                    ? "wording changed after verification and no covering test was updated (update the tests, or run `roadmap accept` alone: the owner answers yes/no)"
                    : "wording changed; tests were updated: run the gate to verify again";
                return s;
            }
            var runAt = outcomes.SelectMany(o => o.Item2).Min(o => o.RunUtc);
            var newest = watched.Select(m => (m, input.ModuleChangedUtc(m))).OrderByDescending(x => x.Item2).First();
            if (newest.Item2 > runAt)
            {
                s.Status = ReqStatus.Unverified;
                s.Detail = "Features/" + newest.m + " changed after the last test run (" + runAt.ToString("HH:mm") + ")";
                return s;
            }
            s.Status = ReqStatus.Verified;
            s.Detail = covers.Count + " test(s) passed at " + runAt.ToString("yyyy-MM-dd HH:mm") + " UTC";
            return s;
        }

        /// <summary>Separation of duties ([critical] only): it counts only with tests sealed by another session, unchanged since, and undisputed.</summary>
        private static bool SealProblem(PlanInputs input, PlanRequirement r, RequirementState s)
        {
            input.Seals.TryGetValue(r.Id, out var seal);
            string who = seal?.Author == "legacy" ? "grandfathered" : "sealed by " + seal?.Author;
            if (seal == null || seal.Void)
                (s.Status, s.Role, s.Detail) = (ReqStatus.Unsealed, TestRoles.Tests,
                    "tests not sealed by an independent session: task start --role tests --req " + r.Id + ", then tests seal " + r.Id);
            else if (seal.RequirementHash != null && seal.RequirementHash != r.Hash)
            {
                // Reworded after it was verified: STALE says more (were the tests updated?); it still needs a new seal to count again.
                if (input.Ledger.TryGetValue(r.Id, out var verified) && verified.RequirementHash != r.Hash)
                    return false;
                (s.Status, s.Role, s.Detail) = (ReqStatus.Unsealed, TestRoles.Tests, "wording changed after the tests were sealed: update them and seal again");
            }
            else if (seal.Ask)
                (s.Status, s.Role, s.Detail, s.OwnerDecides) = (ReqStatus.Disputed, null, "the owner decides the wording (dispute escalated); the arbiter proposes it", true);
            else if (seal.OpenDispute)
                (s.Status, s.Role, s.Detail) = (ReqStatus.Disputed, TestRoles.Arbiter, "disputed by " + seal.DisputedBy + ": an arbiter rules (task start --role arbiter --req " + r.Id + ")");
            else if (seal.Suspect)
                (s.Status, s.Role, s.Detail) = (ReqStatus.Disputed, TestRoles.Arbiter, "re-check: other seals of " + seal.Author + " were fixed by an arbiter");
            else if (seal.Red == "pending")
                (s.Status, s.Role, s.Detail) = (ReqStatus.Unsealed, TestRoles.Tests, "red first not confirmed yet (Unity-only tests): CI/Unity must show them red at " + seal.Commit);
            else if (input.SealHash(r.Id) != seal.Hash)
                (s.Status, s.Role, s.Detail) = (ReqStatus.Tampered, TestRoles.Tests, "covering tests changed after they were " + who + ": revert, or a tests/arbiter session seals them again");
            else
                return false;
            return true;
        }

        private static string Resolve(Plan plan, PlanReport report, FeatureState fs, PlanInputs input, Dictionary<string, string> memo, HashSet<string> visiting)
        {
            if (memo.TryGetValue(fs.Id, out var done))
                return done;
            if (!visiting.Add(fs.Id))
                return FeatureStatus.Blocked; // cycle: ARCH110 reports it
            var f = plan.Feature(fs.Id);
            if (f.Dropped || f.Deferred)
                return memo[fs.Id] = fs.Status = f.Dropped ? FeatureStatus.Dropped : FeatureStatus.Deferred;

            bool depsOk = true;
            foreach (var d in f.Dependencies)
            {
                var target = report.Feature(d.Feature);
                string ts = target == null ? FeatureStatus.Blocked : Resolve(plan, report, target, input, memo, visiting);
                var ds = new DependencyState
                {
                    Kind = d.Kind.ToString().ToLowerInvariant(), To = d.Feature, Contract = d.Contract,
                    Frozen = d.Contract != null && input.ContractFrozen(d.Contract), Satisfied = ts == FeatureStatus.Done
                };
                fs.Dependencies.Add(ds);
                if (ds.Satisfied || d.Kind == DependencyKind.Soft)
                    continue;
                if (d.Kind == DependencyKind.Hard)
                {
                    depsOk = false;
                    fs.BlockedBy.Add("hard " + d.Feature + " chưa DONE");
                }
                else if (f.Rush && ds.Frozen)
                {
                    depsOk = false;
                    fs.Debt.Add("tích hợp với " + d.Feature + " (" + d.Contract + "): đang làm trên interface đã chốt + bản giả, còn nợ tới khi " + d.Feature + " DONE và test chạy lại");
                }
                else
                {
                    depsOk = false;
                    fs.BlockedBy.Add("contract " + d.Feature + " (" + d.Contract + ") " + (ds.Frozen ? "chưa DONE (đánh dấu [rush] để làm trước)" : "chưa chốt interface (không có trong PublicApi.txt)"));
                }
            }

            // [art-risk]: the owner should decide art feasibility early (Sói/Hổ BEFORE 3 skills), but art never blocks dev: a warning only.
            if (f.ArtRisk)
                foreach (var r in fs.Requirements.Where(x => plan.Requirement(x.Id).IsFeasibility && !ReqStatus.IsDone(x.Status)))
                    fs.ArtRisk.Add("art feasibility " + r.Id + " chưa được người duyệt (gameplay vẫn làm được; quyết sớm để tránh làm lại)");

            // What this feature asked other modules for (CRs): owed until delivered; it works on a fake meanwhile, never waits idle.
            foreach (var debt in CrPlanning.Debt(report, input.Crs, fs.Id))
            {
                depsOk = false;
                fs.Debt.Add(debt);
            }

            bool allReq = fs.Total > 0 && fs.Verified == fs.Total; // art debt is tracked apart (ArtComplete): it never holds DONE back
            bool started = fs.Requirements.Any(r => r.Status != ReqStatus.NoTest && r.Status != ReqStatus.NeedsReview);
            string status = allReq && depsOk ? FeatureStatus.Done
                : fs.BlockedBy.Count > 0 && !started ? FeatureStatus.Blocked
                : started ? FeatureStatus.InProgress
                : FeatureStatus.Planned;
            visiting.Remove(fs.Id);
            return memo[fs.Id] = fs.Status = status;
        }

        private static int Depth(Plan plan, PlanFeature f, int guard)
        {
            if (guard > 50)
                return guard;
            var deps = f.Dependencies.Where(d => d.Kind != DependencyKind.Soft).Select(d => plan.Feature(d.Feature)).Where(x => x != null).ToList();
            return deps.Count == 0 ? 0 : 1 + deps.Max(d => Depth(plan, d, guard + 1));
        }
    }
}
