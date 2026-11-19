using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    public sealed class CrClassifierTests
    {
        private const string Api = "interface Game.B.Contracts.IB\n  System.Int32 Count { get; }\nclass Game.B.Contracts.BAssets\n";

        [Test]
        public void NewType_IsAdditive_TheGateApproves()
        {
            var (kind, error) = CrClassifier.Classify(Api, new[] { "interface Game.B.Contracts.INew", "  System.Void Add(System.Int32 n)" }, false);
            Assert.That(error, Is.Null);
            Assert.That(kind, Is.EqualTo(CrClassifier.Additive));
            Assert.That(CrClassifier.Route(kind), Is.EqualTo(CrClassifier.Gate));
        }

        [Test]
        public void MemberOnAnExistingType_IsBreaking_ItBreaksImplementersAndFakes()
        {
            var (kind, _) = CrClassifier.Classify(Api, new[] { "interface Game.B.Contracts.IB", "  System.Void Add(System.Int32 n)" }, false);
            Assert.That(kind, Is.EqualTo(CrClassifier.Breaking));
            Assert.That(CrClassifier.Route(kind), Is.EqualTo(CrClassifier.ArbiterRoute));
        }

        [Test]
        public void RemovingALine_IsBreaking()
        {
            Assert.That(CrClassifier.Classify(Api, new[] { "- System.Int32 Count { get; }" }, false).kind, Is.EqualTo(CrClassifier.Breaking));
        }

        [Test]
        public void NoApi_IsAChangeOfMeaning_TheOwnerDecides()
        {
            var (kind, _) = CrClassifier.Classify(Api, new string[0], false);
            Assert.That(kind, Is.EqualTo(CrClassifier.Meaning));
            Assert.That(CrClassifier.Route(kind), Is.EqualTo(CrClassifier.Owner));
        }

        [Test]
        public void Core_IsStricter_AdditiveNeedsAnArbiter_BreakingNeedsTheOwner()
        {
            Assert.That(CrClassifier.Route(CrClassifier.Classify("", new[] { "class Game.Core.NewThing" }, true).kind), Is.EqualTo(CrClassifier.ArbiterRoute));
            Assert.That(CrClassifier.Route(CrClassifier.Classify(Api, new[] { "- System.Int32 Count { get; }" }, true).kind), Is.EqualTo(CrClassifier.Owner));
        }

        [Test]
        public void MemberLineWithoutItsType_IsRefused()
        {
            Assert.That(CrClassifier.Classify(Api, new[] { "  System.Void Add()" }, false).error, Does.Contain("type header"));
        }

        [Test]
        public void ApiEdit_MayChangeExactlyTheCrLines()
        {
            var cr = new[] { "interface Game.B.Contracts.INew", "  System.Void Add(System.Int32 n)" };
            string after = Api + "interface Game.B.Contracts.INew\n  System.Void Add(System.Int32 n)\n";
            Assert.That(CrClassifier.ApiDiffViolations(Api, after, cr), Is.Empty);
            Assert.That(CrClassifier.ApiDiffViolations(Api, after + "  System.Void Sneak()\n", cr), Has.Some.Contains("Sneak"));
            Assert.That(CrClassifier.ApiDiffViolations(Api, after.Replace("  System.Int32 Count { get; }\n", ""), cr), Has.Some.Contains("removed"));
        }
    }

    public sealed class CrLogTests
    {
        [Test]
        public void FoldsTheLog_ApprovedCrsBecomeRequirements_ApiSurvivesTheRoundTrip()
        {
            var crs = new Dictionary<string, CrEntry>(StringComparer.Ordinal);
            string api = CrLog.EncodeApi(new[] { "interface Game.B.Contracts.INew", "  System.Void Add(System.Int32 n)" });
            CrLog.Apply(crs, "file CR-1 from=F-A to=F-B module=B kind=additive task=T1 session=s1 ts=2026-01-01T00:00:00Z api=" + api + " reason=need INew for coins");
            Assert.That(crs["CR-1"].Status, Is.EqualTo(CrLog.Open));
            Assert.That(crs["CR-1"].IsRequirement, Is.False);
            CrLog.Apply(crs, "approve CR-1 by=gate ts=2026-01-01T00:00:01Z reason=additive");
            CrLog.Apply(crs, "approve CR-9 by=gate ts=x reason=never filed");
            Assert.That(crs["CR-1"].IsRequirement, Is.True);
            Assert.That(crs["CR-1"].Text, Is.EqualTo("need INew for coins"));
            Assert.That(crs["CR-1"].Api, Is.EqualTo(new[] { "interface Game.B.Contracts.INew", "  System.Void Add(System.Int32 n)" }));
            Assert.That(crs.ContainsKey("CR-9"), Is.False, "a decision about a CR that was never filed is ignored");
        }
    }

    /// <summary>End to end on a throw-away git repo with a plan: A needs something from B.</summary>
    public sealed class CrFlowTests
    {
        private string _root;
        private const string BApi = "interface Game.B.Contracts.IB\n  System.Int32 Count { get; }\n";
        private const string ApiPath = "UnityProject/Assets/_Game/Features/B/Contracts/PublicApi.txt";

        private void Git(string args)
        {
            var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        private void Write(string rel, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, rel)));
            File.WriteAllText(Path.Combine(_root, rel), text);
        }

        private void Session(string s) => Write(".agent/" + TestSeal.SessionFile, s);

        private static HookInput H(string tool, Dictionary<string, string> input) =>
            HookInput.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = "sessA", ["tool_name"] = tool, ["tool_use_id"] = "tu1", ["tool_input"] = input
            }));

        private int Task(params string[] args) => AgentCli.Task(_root, new[] { "task" }.Concat(args).ToArray());

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "crflow-" + Guid.NewGuid().ToString("N"));
            Write("Docs/Plan/features.md", "# Plan: T\n## F-A  Alpha [P0] [modules: A]\n- R-A  A works\n## F-B  Beta [P2] [modules: B]\n- R-B  B works\n");
            Write("UnityProject/Assets/_Game/Features/A/Logic/X.cs", "class X {}\n");
            Write("UnityProject/Assets/_Game/Features/B/Logic/Y.cs", "class Y {}\n");
            Write("UnityProject/Assets/_Game/Features/B/Logic/Domain/Rules.cs", "class Rules {}\n");
            Write(ApiPath, BApi);
            Git("init -q");
            Git("-c user.email=a@b -c user.name=t add -A");
            Git("-c user.email=a@b -c user.name=t commit -qm init");
            Recorder.Root = _root;
            Recorder.Session = "sessA";
            Session("sessA");
        }

        [TearDown]
        public void TearDown()
        {
            Recorder.Root = null;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        [Test]
        public void AdditiveNeed_IsApprovedByTheGate_BecomesBsRequirement_AndAOwesIt()
        {
            Assert.That(Task("start", "A work", "--req", "R-A"), Is.EqualTo(0));
            Assert.That(Task("need", "B", "an INew to add coins", "--api", "interface Game.B.Contracts.INew"), Is.EqualTo(0));

            var cr = CrStore.Load(_root)["CR-1"];
            Assert.That(cr.Status, Is.EqualTo(CrLog.Approved));
            Assert.That((cr.From, cr.To, cr.Module, cr.Kind), Is.EqualTo(("F-A", "F-B", "B", CrClassifier.Additive)));
            var plan = PlanParser.Load(_root);
            Assert.That(plan.Requirement("CR-1").Feature, Is.EqualTo("F-B"));
            Assert.That(plan.Requirement("CR-1").Tags, Does.Contain("cr"));

            var report = PlanStatus.Compute(PlanInputs.Load(_root));
            Assert.That(report.Feature("F-A").Debt, Has.Some.Contains("CR-1"), "A works on a fake and owes CR-1");
            Assert.That(report.Feature("F-A").Status, Is.Not.EqualTo(FeatureStatus.Done));
            var next = PlanScheduler.Next(report);
            var item = next.Single(i => i.Requirements.Contains("CR-1"));
            Assert.That(item.Feature, Is.EqualTo("F-B"));
            Assert.That(item.Lane, Is.EqualTo("now"));
            Assert.That(CrPlanning.Rank(report, item, 2), Is.EqualTo(0), "CR-1 runs at the priority of F-A (P0), not F-B's P2");
        }

        [Test]
        public void TheGrant_LetsTheAskerWriteOnlyTheCrLinesAndASkeleton()
        {
            Task("start", "A work", "--req", "R-A");
            Task("need", "B", "an INew", "--api", "interface Game.B.Contracts.INew", "--api", "  System.Void Add(System.Int32 n)");
            var task = TaskStore.Load(_root);
            Assert.That(ScopePolicy.Check(task, "UnityProject/Assets/_Game/Features/B/Contracts/INew.cs").Allowed, Is.True);
            Assert.That(ScopePolicy.Check(task, ApiPath).Allowed, Is.True);
            Assert.That(ScopePolicy.Check(task, "UnityProject/Assets/_Game/Features/B/Logic/Y.cs").Allowed, Is.False, "B's code stays B's");

            Write(ApiPath, BApi + "interface Game.B.Contracts.INew\n  System.Void Add(System.Int32 n)\n");
            Assert.That(CrStore.GrantFindings(_root, task, ApiPath), Is.Empty);
            Write(ApiPath, BApi + "interface Game.B.Contracts.INew\n  System.Void Add(System.Int32 n)\n  System.Void Extra()\n");
            Assert.That(CrStore.GrantFindings(_root, task, ApiPath), Has.Some.Contains("Extra"));
            Write("UnityProject/Assets/_Game/Features/B/Contracts/INew.cs", "namespace Game.B.Contracts\n{\npublic interface INew\n{\nvoid Add(int n) { Total += n; }\n}\n}\n");
            Assert.That(CrStore.GrantFindings(_root, task, "UnityProject/Assets/_Game/Features/B/Contracts/INew.cs"), Is.Not.Empty, "no implementation under a grant");
        }

        [Test]
        public void BreakingNeed_WaitsForAnArbiter_WhoIsNotTheAsker()
        {
            Task("start", "A work", "--req", "R-A");
            Task("need", "B", "IB must also Add", "--api", "interface Game.B.Contracts.IB", "--api", "  System.Void Add(System.Int32 n)");
            Assert.That(CrStore.Load(_root)["CR-1"].Status, Is.EqualTo(CrLog.Open));
            Assert.That(PlanParser.Load(_root).Requirement("CR-1"), Is.Null, "not a requirement before it is approved");
            Assert.That(PlanStatus.Compute(PlanInputs.Load(_root)).Gaps, Has.Some.Matches<Gap>(g => g.Kind == "CR_RULE" && g.Detail.Contains("cr rule CR-1")));

            Assert.That(CrCli.Cli(_root, new[] { "cr", "rule", "CR-1", "keep", "fine" }), Is.EqualTo(3), "the asker can't rule on its own CR");
            Session("sessArbiter");
            Assert.That(CrCli.Cli(_root, new[] { "cr", "rule", "CR-1", "keep", "only A uses IB; one fake to update" }), Is.EqualTo(0));
            Assert.That(CrStore.Load(_root)["CR-1"].DecidedBy, Is.EqualTo("arbiter:sessArbiter"));
            Assert.That(PlanParser.Load(_root).Requirement("CR-1"), Is.Not.Null);
        }

        [Test]
        public void ChangeOfMeaning_GoesToTheOwnersList()
        {
            Task("start", "A work", "--req", "R-A");
            Assert.That(Task("need", "B", "B should count bonus answers twice"), Is.EqualTo(0));
            Assert.That(CrStore.Load(_root)["CR-1"].Kind, Is.EqualTo(CrClassifier.Meaning));
            var report = PlanStatus.Compute(PlanInputs.Load(_root));
            Assert.That(report.Gaps, Has.Some.Matches<Gap>(g => g.Kind == "CR_DECIDE" && g.Actor == "human"));
            Assert.That(report.Feature("F-A").Debt, Has.Some.Contains("chờ duyệt CR-1"));
            Assert.That(AgentHooks.ForbiddenCommand("./Tools/test-gate.sh cr decide"), Is.Not.Null, "deciding is the owner's");
        }

        [Test]
        public void NeedInsideYourOwnScope_IsRefused_JustDoIt()
        {
            Task("start", "A work", "--req", "R-A");
            Assert.That(Task("need", "A", "something"), Is.EqualTo(1));
            Assert.That(CrStore.Load(_root), Is.Empty);
        }

        [Test]
        public void CoversOnAnApprovedCr_IsAKnownRequirement_Arch111()
        {
            Task("start", "A work", "--req", "R-A");
            Task("need", "B", "an INew", "--api", "interface Game.B.Contracts.INew");
            Write("UnityProject/Assets/_Game/Features/B/Tests/BTests.cs", "class BTests { [Test, Covers(\"CR-1\")] public void Adds() { } [Test, Covers(\"CR-7\")] public void Nope() { } }\n");
            var arch111 = PlanRules.Check(_root).Where(v => v.RuleId == "ARCH111").ToList();
            Assert.That(arch111.Select(v => v.Message), Has.None.Contains("\"CR-1\""));
            Assert.That(arch111.Select(v => v.Message), Has.Some.Contains("\"CR-7\""));
        }

        [Test]
        public void HandEditOfTheCrLog_IsABreach_TheGatesWriteIsNot()
        {
            Task("start", "A work", "--req", "R-A");
            Task("need", "B", "an INew", "--api", "interface Game.B.Contracts.INew");
            var findings = AgentHooks.StopChecks(_root, H(null, new Dictionary<string, string>()), _ => null);
            Assert.That(findings.Where(f => f.Contains(CrLog.Dir)), Is.Empty, string.Join("\n", findings));

            File.AppendAllText(Path.Combine(_root, CrLog.FileOf("F-B")), "approve CR-2 by=owner ts=x reason=forged\n");
            AgentHooks.Post(_root, H("Bash", new Dictionary<string, string> { ["command"] = "echo >> log" }), _ => new List<string>());
            var state = WorkSnapshot.HashFile(Path.Combine(_root, CrLog.FileOf("F-B")));
            Assert.That(CrStore.IsGateWrite(_root, CrLog.FileOf("F-B"), state), Is.False);
            Assert.That(ScopePolicy.Check(TaskStore.Load(_root), CrLog.FileOf("F-B")).Allowed, Is.False, "Docs/Plan/CRs is the gate's");
        }

        [Test]
        public void MaintenanceTask_ChangingWhatAFeaturePromises_IsUntraced()
        {
            Assert.That(Task("start", "tidy B", "--feature", "B"), Is.EqualTo(0));
            Assert.That(TaskStore.Load(_root).Provenance, Is.EqualTo("maintenance"));
            Write("UnityProject/Assets/_Game/Features/B/Logic/Domain/Rules.cs", "class Rules { int Bonus => 2; }\n");
            Write("UnityProject/Assets/_Game/Features/B/Logic/Y.cs", "class Y { }\n// tidy\n");
            foreach (var p in new[] { "UnityProject/Assets/_Game/Features/B/Logic/Domain/Rules.cs", "UnityProject/Assets/_Game/Features/B/Logic/Y.cs" })
                AgentHooks.Post(_root, H("Edit", new Dictionary<string, string> { ["file_path"] = Path.Combine(_root, p) }), _ => new List<string>());
            var findings = AgentHooks.StopChecks(_root, H(null, new Dictionary<string, string>()), _ => null);
            Assert.That(findings, Has.Some.StartsWith("UNTRACED_CHANGE").And.Contains("Domain/Rules.cs"));
            Assert.That(findings.Where(f => f.Contains("Logic/Y.cs")), Is.Empty, "refactoring outside Domain/Contracts is maintenance");
        }

        [Test]
        public void TaskFromACr_HasProvenance_AndMayChangeBsDomain()
        {
            Task("start", "A work", "--req", "R-A");
            Task("need", "B", "an INew", "--api", "interface Game.B.Contracts.INew");
            Task("close");
            Assert.That(Task("start", "B does CR-1", "--from", "CR-1"), Is.EqualTo(0));
            var task = TaskStore.Load(_root);
            Assert.That(task.Features, Is.EqualTo(new[] { "B" }));
            Assert.That(task.Provenance, Is.EqualTo("plan"));
            Assert.That(task.CrGrants.ContainsKey("CR-1"), Is.True, "B's session may write the CR's PublicApi lines");
            Assert.That(AgentHooks.Untraced(task, "UnityProject/Assets/_Game/Features/B/Logic/Domain/Rules.cs"), Is.Empty);
        }

        [Test]
        public void MessagingAnotherSession_IsLogged_WithTheSendersTask()
        {
            Task("start", "A work", "--req", "R-A");
            int code = AgentHooks.Pre(_root, H("SendMessage", new Dictionary<string, string> { ["to"] = "b-session", ["message"] = "please add Add() to IB" }));
            Assert.That(code, Is.EqualTo(0), "logged, not blocked: the hard check is provenance at the receiver");
            var e = EventLog.For(_root).ReadAll().Single(x => x.Type == EventTypes.AgentMessage);
            Assert.That(e.Get("to"), Is.EqualTo("b-session"));
            Assert.That(e.Get("fromTask"), Is.EqualTo(TaskStore.Load(_root).Id));
        }
    }
}
