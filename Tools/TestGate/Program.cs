// Incremental Test Gate for this Unity project.
//
// Each changed file is classified by its ROLE (ChangeImpact, in the rule engine): the rules make roles exact
// (views hold no logic, static layout only in the view's recipe/prefab, one folder per view), so:
//   G0  Static rules             always, in-process, milliseconds.
//   G1  dotnet test Tools/LogicTests   when pure code/tests/rules changed (all pure tests: seconds; a subset saves nothing).
//   UI  Layout gate per VIEW     Unity EditMode, -testCategory "View:F.V": L2 constraints + L3 text snapshot of
//                                only the affected views. No scene, no game boot. ("I moved it 2px" = this only.)
//   G2  Unity EditMode tests    ┐ only for integration files (installers, Bootstrap, scenes, settings, asmdefs),
//   G3  Unity PlayMode tests    ┘ and only the test assemblies affected according to the .asmdef graph.
//   G4  --full                  everything (pre-merge / nightly / release). Unknown paths → full (fail-closed).
//
// Token/cost savers for AI agents:
//   - prints ONLY failures (test name + first lines of the message), never full logs;
//   - Unity retry budget: after 2 consecutive Unity-gate failures it refuses to run and tells the
//     agent to report to a human (`reset` clears it).
//
// Commands:
//   plan | run [--full] [--base <ref>] | reset
//   check [--all|--staged] [paths...]  static architecture rules on changed/staged/given files or everything (ms, no build)
//   refs <folder>                 incoming asset + assembly references (run before deleting a feature)
//   graph [--write|feature F|impact F|di F|diff [base]]   architecture map & queries (compact text)
//   vendor-hash <folder>          sha256 to register a vendor folder in Tools/ArchitecturePolicy.txt (owners)
//   new-feature Name [--contracts] scaffold a feature that already satisfies every rule
//   new-view Feature View          scaffold a view/popup testable in isolation (view, layout recipe, fixtures, cases)
//   task start|request|approve|show|close   permission unit = one feature (wider scope: a human approves)
//   roadmap status|gaps|next|propose|accept   product plan (Docs/Plan/features.md): computed status, gaps, parallel lanes
//   files [orphans|clean|unused|du]  loose/orphan/junk files (derived from git + event log), unused assets (GUID graph), disk use
//   tests seal|dispute|rule|status   requirement tests by roles: a tests session seals (lint + red first), impl codes, arbiter rules
//   art status|accept             art slots: computed level (graybox/candidate/approved/final), human approval by content hash
//   events verify|tail · reconcile · dash · serve   tamper-evident event log → deterministic dashboard (Agent/)
//   hook-pre                      PreToolUse: scope guard blocks writes BEFORE they happen; snapshot before Bash
//   hook-post                     PostToolUse: record tool/file events (Bash edits via git snapshot diff) + per-file rules
//   hook-stop                     Stop: reconcile, scope of every change, test-weakening guard, then the gate
//   hook-session / hook-subagent-stop   SessionStart context (task, last gate, chain) · subagent lifetime

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TestGate.AgentControl;
using Game.Architecture.Rules;

internal static class Program
{
    private const int MaxUnityFailures = 2;
    private const int MaxStopBlocks = 3;
    private const int MaxMessageLines = 3;

    private static string _root;

    private static int Main(string[] args)
    {
        // Vietnamese plan text: the Windows console default code page turns it into '?', also in hook output the agent reads.
        Console.OutputEncoding = new UTF8Encoding(false);
        _root = FindRepoRoot();
        Recorder.Root = _root;
        string command = args.Length > 0 ? args[0] : "plan";
        bool full = args.Contains("--full");
        string baseRef = ArgValue(args, "--base");

        try
        {
            switch (command)
            {
                case "plan":
                    PrintPlan(BuildPlan(baseRef, full));
                    return 0;
                case "run":
                    return Run(BuildPlan(baseRef, full));
                case "check":
                    return Check(args.Skip(1).ToArray(), baseRef);
                case "refs":
                    return Refs(args.Length > 1 ? args[1] : null);
                case "graph":
                    return GraphCommands.Run(_root, args, (f, a) => Exec(f, a, quiet: true));
                case "vendor-hash":
                    if (args.Length < 2) { Console.Error.WriteLine("Usage: vendor-hash <folder>"); return 64; }
                    Console.WriteLine("sha256=" + ThirdPartyRules.HashFolder(Path.Combine(_root, args[1])));
                    return 0;
                case "new-feature":
                    return Scaffold.NewFeature(_root, args);
                case "new-view":
                    return ViewScaffold.NewView(_root, args);
                case "hook-pre":
                    return AgentHooks.Pre(_root, HookInput.Parse(ReadStdin()));
                case "hook-post":
                    return AgentHooks.Post(_root, HookInput.Parse(ReadStdin()), CheckEditedFiles);
                case "hook-stop":
                    return HookStop();
                case "hook-session":
                    return AgentHooks.SessionStart(_root, HookInput.Parse(ReadStdin()));
                case "hook-subagent-stop":
                    return AgentHooks.SubagentStop(_root, HookInput.Parse(ReadStdin()));
                case "task":
                    return AgentCli.Task(_root, args);
                case "tests":
                    return TestSeal.Cli(_root, args);
                case "events":
                    return AgentCli.Events(_root, args);
                case "reconcile":
                    return AgentCli.Reconcile(_root);
                case "files":
                    return TestGate.Files.FilesCli.Run(_root, args);
                case "art":
                    return TestGate.Planning.ArtCli.Run(_root, args);
                case "roadmap":
                    return TestGate.Planning.RoadmapCli.Run(_root, args);
                case "dash":
                    return AgentCli.Dash(_root, args);
                case "serve":
                    return AgentCli.Serve(_root, args);
                case "reset":
                    WriteUnityFailures(0);
                    Console.WriteLine("Unity retry budget reset.");
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: TestGate plan|run|reset|check|refs|graph|vendor-hash|new-feature|new-view|roadmap|art|files|task|events|reconcile|dash|serve|hook-pre|hook-post|hook-stop|hook-session|hook-subagent-stop");
                    return 64;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("TestGate error: " + e.Message);
            return 70;
        }
    }

    // ------------------------------------------------------------------ plan

    private sealed class Asmdef
    {
        public string Name;
        public string Dir;            // relative, '/' separated, e.g. Assets/_Game/Features/MathQuiz/Logic
        public List<string> References = new List<string>();
        public bool Pure;             // noEngineReferences
        public bool EditorOnly;       // includePlatforms == [Editor]
        public bool IsTest => Name.EndsWith(".Tests", StringComparison.Ordinal);
    }

    private sealed class Plan
    {
        public bool Full;
        public string Reason;
        public List<string> ChangedFiles = new List<string>();
        public SortedSet<string> ChangedAssemblies = new SortedSet<string>();
        public SortedSet<string> AffectedAssemblies = new SortedSet<string>();
        public List<string> EditModeTests = new List<string>();
        public List<string> PlayModeTests = new List<string>();
        public bool UnityRequired;
        public List<string> UnityReasons = new List<string>();
        public bool DotnetTests;
        public SortedSet<string> Views = new SortedSet<string>(StringComparer.Ordinal);
        public SortedSet<string> Slots = new SortedSet<string>(StringComparer.Ordinal);
        public List<FileImpact> Impacts = new List<FileImpact>();
    }

    private const string LayoutGateAssembly = "Game.Core.Editor.Tests";

    private static Plan BuildPlan(string baseRef, bool full)
    {
        var asmdefs = LoadAsmdefs();
        var plan = new Plan { Full = full };

        List<string> changed = full ? null : ChangedFiles(baseRef, out plan.Reason);
        var impact = new ChangeImpact(_root);
        if (changed != null)
        {
            plan.Impacts = changed.Select(impact.Classify).ToList();
            var unknown = plan.Impacts.FirstOrDefault(i => i.Has(ChangeRole.Unknown));
            if (unknown != null)
            {
                changed = null;
                plan.Reason = "unknown file role, fail-closed: " + unknown.Path;
            }
        }

        if (changed == null)
        {
            plan.Full = true;
            plan.Reason ??= "--full";
            plan.DotnetTests = true;
            foreach (var v in impact.Views)
                plan.Views.Add(v.Id);
            foreach (var d in ArtRules.Declarations(_root))
                plan.Slots.Add(d.Id);
            foreach (var a in asmdefs.Where(a => a.IsTest && a.Name != LayoutGateAssembly))
                (a.EditorOnly ? plan.EditModeTests : plan.PlayModeTests).Add(a.Name);
            plan.UnityRequired = true;
            plan.UnityReasons.Add("full run");
            return plan;
        }

        plan.ChangedFiles = changed;
        foreach (var i in plan.Impacts)
        {
            plan.DotnetTests |= i.Has(ChangeRole.Pure);
            foreach (var v in i.Views)
                plan.Views.Add(v);
            foreach (var a in i.Slots)
                plan.Slots.Add(a);
        }

        // Only integration files go through the assembly graph (views have their own, much smaller gate).
        foreach (var file in plan.Impacts.Where(i => i.Has(ChangeRole.Integration)).Select(i => i.Path))
        {
            var owner = OwnerOf(file, asmdefs);
            if (owner != null)
            {
                plan.ChangedAssemblies.Add(owner.Name);
                if (!owner.Pure)
                    plan.UnityReasons.Add(file + " (Unity-side assembly " + owner.Name + ")");
                if (file.EndsWith(".asmdef", StringComparison.Ordinal))
                    plan.UnityReasons.Add(file + " (assembly definition changed)");
            }
            else if (IsUnityAsset(file))
            {
                plan.UnityReasons.Add(file + " (Unity asset / settings)");
            }
        }

        // Reverse dependency closure: everything that (transitively) references a changed assembly.
        var dependents = asmdefs.ToDictionary(a => a.Name, a => new List<string>());
        foreach (var a in asmdefs)
            foreach (var r in a.References.Where(dependents.ContainsKey))
                dependents[r].Add(a.Name);

        var queue = new Queue<string>(plan.ChangedAssemblies);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!plan.AffectedAssemblies.Add(name))
                continue;
            if (dependents.TryGetValue(name, out var list))
                foreach (var d in list)
                    queue.Enqueue(d);
        }

        plan.UnityRequired = plan.UnityReasons.Count > 0;
        if (plan.UnityRequired)
        {
            foreach (var a in asmdefs.Where(a => a.IsTest && plan.AffectedAssemblies.Contains(a.Name) && a.Name != LayoutGateAssembly))
                (a.EditorOnly ? plan.EditModeTests : plan.PlayModeTests).Add(a.Name);
        }

        return plan;
    }

    private static void PrintPlan(Plan plan)
    {
        Console.WriteLine("== Test plan" + (plan.Full ? " (FULL: " + plan.Reason + ")" : ""));
        if (!plan.Full)
        {
            Console.WriteLine("Changed files: " + plan.ChangedFiles.Count);
            foreach (var i in plan.Impacts.Take(15))
                Console.WriteLine("  " + i);
            if (plan.Impacts.Count > 15)
                Console.WriteLine("  ... and " + (plan.Impacts.Count - 15) + " more");
        }

        Console.WriteLine("G0     static rules: ALWAYS (in-process, ms)");
        Console.WriteLine("G1     dotnet test Tools/LogicTests: " + (plan.DotnetTests ? "yes" : "not needed (no pure code, tests or rules changed)"));
        Console.WriteLine("UI     layout gate (Unity EditMode, per view): " + (plan.Views.Count == 0
            ? "not needed"
            : Join(plan.Views) + "  [-testCategory \"" + ViewCategories(plan) + "\"]"));
        Console.WriteLine("ART    art gate (Unity EditMode, per slot): " + (plan.Slots.Count == 0 ? "not needed" : Join(plan.Slots)));
        if (!plan.UnityRequired)
        {
            Console.WriteLine("G2/G3  Unity integration: not needed (no installer, Bootstrap, scene, settings or asmdef changed)");
            return;
        }

        foreach (var r in plan.UnityReasons.Distinct().Take(10))
            Console.WriteLine("       Unity needed: " + r);
        Console.WriteLine("G2     Unity EditMode: " + Join(plan.EditModeTests));
        Console.WriteLine("G3     Unity PlayMode: " + Join(plan.PlayModeTests));
    }

    private static string ViewCategories(Plan plan) => string.Join(";", plan.Views.Select(v => "View:" + v.Replace('/', '.')).Concat(plan.Slots.Select(s => "Art:" + s)));

    // ------------------------------------------------------------------ run

    private static bool _unityPending;
    private static List<RuleViolation> _lastViolations = new List<RuleViolation>();
    private static List<string> _lastFailedTests = new List<string>();
    private static string _lastFailedStage;

    /// <summary>Runs the gate and records it: GATE_STARTED, TEST_RESULT per suite (with the result file's hash), GATE_FINISHED.</summary>
    private static int Run(Plan plan)
    {
        _unityPending = false;
        _lastViolations = new List<RuleViolation>();
        _lastFailedTests = new List<string>();
        _lastFailedStage = null;
        string summary = (plan.Full ? "FULL (" + plan.Reason + ")" : plan.ChangedFiles.Count + " changed file(s)") +
                         "\nG1 dotnet: " + (plan.DotnetTests ? "yes" : "no") +
                         "\nUI layout: " + (plan.Views.Count > 0 ? string.Join(", ", plan.Views) : "-") +
                         "\nG2/G3 Unity: " + (plan.UnityRequired ? string.Join(", ", plan.EditModeTests.Concat(plan.PlayModeTests)) : "-");
        Recorder.Emit(Recorder.New(EventTypes.GateStarted, "testgate").With("plan", summary));
        int code = RunGate(plan);
        string result = code == 0 ? (_unityPending ? "pending" : "passed") : "failed";
        Recorder.Emit(Recorder.New(EventTypes.GateFinished, "testgate").With("result", result).With("exit", code.ToString()).With("plan", summary));
        return code;
    }

    private static void RecordTests(string suite, string result, int passed, int failed, int total, string resultsFile, string note = null)
    {
        string rel = resultsFile != null && File.Exists(resultsFile) ? Rel(resultsFile) : null;
        Recorder.Emit(Recorder.New(EventTypes.TestResult, "testgate").With("suite", suite).With("result", result)
            .With("passed", passed.ToString()).With("failed", failed.ToString()).With("total", total.ToString())
            .With("resultsFile", rel).With("resultsHash", rel != null ? WorkSnapshot.HashFile(resultsFile) : null).With("note", note));
    }

    private static int RunGate(Plan plan)
    {
        PrintPlan(plan);
        Console.WriteLine();

        // G0a: static rules on the changed files first: milliseconds, no build. Fail fast.
        var violations = plan.Full ? ArchitectureScanner.ScanAll(_root) : ArchitectureScanner.ScanFiles(_root, plan.ChangedFiles);
        RecordTests("G0 static rules", violations.Count == 0 ? "passed" : "failed", 0, violations.Count, violations.Count, null,
            violations.Count == 0 ? "no violation" : string.Join("; ", violations.Take(3)));
        if (violations.Count > 0)
        {
            _lastViolations = violations;
            _lastFailedStage = "G0";
            Console.WriteLine("G0 FAILED: " + violations.Count + " architecture violation(s)");
            Console.WriteLine(ArchitectureScanner.Format(violations));
            PrintHumanHints(violations);
            return 1;
        }

        if (plan.DotnetTests && !RunDotnetTests())
            return 1;

        bool unityWork = plan.Views.Count > 0 || plan.Slots.Count > 0 || (plan.UnityRequired && (plan.EditModeTests.Count > 0 || plan.PlayModeTests.Count > 0));
        if (!unityWork)
        {
            Console.WriteLine("GATE PASSED (Unity gate not required).");
            return 0;
        }

        int failures = ReadUnityFailures();
        if (failures >= MaxUnityFailures)
        {
            Console.WriteLine("STOP: the Unity gate failed " + failures + " times in a row.");
            Console.WriteLine("Do not retry. Report the failures above to a human (or run `TestGate reset` after a real fix).");
            return 4;
        }

        string unity = Environment.GetEnvironmentVariable("UNITY_PATH");
        if (string.IsNullOrEmpty(unity))
        {
            _unityPending = true;
            RecordTests("Unity", "pending", 0, 0, 0, null, "UNITY_PATH not set" + (plan.Views.Count > 0 ? "; layout gate pending for " + string.Join(", ", plan.Views) : ""));
            Console.WriteLine("UNITY GATE NOT RUN: UNITY_PATH is not set. " + (plan.DotnetTests ? "G0+G1 passed." : "G0 passed (G1 not needed).") + (plan.Views.Count > 0 ? " Pending layout gate: " + Join(plan.Views) + "." : "") + (plan.Slots.Count > 0 ? " Pending art gate: " + Join(plan.Slots) + "." : ""));
            Console.WriteLine("Report this as 'Unity tests pending' (CI or a human must run them).");
            return 0;
        }

        bool ok = true;
        if (plan.Views.Count > 0 || plan.Slots.Count > 0)
            ok &= RunUnityTests(unity, "EditMode", new List<string> { LayoutGateAssembly }, ViewCategories(plan), "Layout+Art");
        if (ok && plan.UnityRequired && plan.EditModeTests.Count > 0)
            ok &= RunUnityTests(unity, "EditMode", plan.EditModeTests);
        if (ok && plan.UnityRequired && plan.PlayModeTests.Count > 0)
            ok &= RunUnityTests(unity, "PlayMode", plan.PlayModeTests);

        WriteUnityFailures(ok ? 0 : failures + 1);
        Console.WriteLine(ok ? "GATE PASSED." : "GATE FAILED (Unity). Attempts left: " + (MaxUnityFailures - failures - 1));
        return ok ? 0 : 2;
    }

    private static bool RunDotnetTests()
    {
        string results = Path.Combine(_root, "Dashboard", "Data", "Dotnet");
        if (Directory.Exists(results))
            Directory.Delete(results, true);

        var (code, output) = Exec("dotnet", "test Tools/LogicTests --nologo -v q --logger \"trx;LogFileName=gate.trx\" --results-directory \"" + results + "\"");

        var trx = Directory.Exists(results) ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (trx == null)
        {
            RecordTests("G1 dotnet", "failed", 0, 0, 0, null, "build error");
            _lastFailedStage = "build";
            Console.WriteLine("G0+G1 FAILED: build error");
            foreach (var line in output.Split('\n').Where(l => l.Contains(": error ")).Distinct().Take(20))
                Console.WriteLine("  " + ShortenPath(line.Trim()));
            return false;
        }

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var doc = XDocument.Load(trx);
        var all = doc.Descendants(ns + "UnitTestResult").ToList();
        // Sealed tests of requirements never verified yet are red BY DESIGN (red first, the impl session is on it): reported, not a
        // failure, so one feature's pending work never blocks the gate of everyone else. A verified requirement turning red is a failure.
        var classes = doc.Descendants(ns + "UnitTest").ToDictionary(u => (string)u.Attribute("id"), u => (string)u.Element(ns + "TestMethod")?.Attribute("className") ?? "");
        var awaiting = TestSeal.AwaitingImpl(_root);
        var allFailed = all.Where(r => (string)r.Attribute("outcome") == "Failed").ToList();
        var expected = allFailed.Where(r => awaiting.Contains(TestSeal.KeyOf((string)r.Attribute("testName"), classes.TryGetValue((string)r.Attribute("testId") ?? "", out var cn) ? cn : ""))).ToList();
        var failed = allFailed.Except(expected).ToList();
        bool green = failed.Count == 0 && (code == 0 || expected.Count > 0);
        RecordTests("G1 dotnet", green ? "passed" : "failed", all.Count - allFailed.Count, failed.Count, all.Count, trx,
            failed.Count > 0 ? string.Join("; ", failed.Take(3).Select(r => (string)r.Attribute("testName")))
            : expected.Count > 0 ? expected.Count + " sealed test(s) red by design, awaiting impl" : null);

        if (green)
        {
            try
            {
                var plan = PlanParser.Load(_root);
                if (plan != null)
                {
                    var index = TestGate.Planning.TestResultsIndex.Load(_root);
                    int n = TestGate.Planning.PlanLedger.RecordVerified(_root, plan, PlanRules.Covers(_root), index);
                    if (n > 0)
                        Console.WriteLine("roadmap: " + n + " requirement(s) recorded as verified");
                    foreach (var id in TestSeal.MarkVerified(_root, plan, index))
                        Console.WriteLine("seal: " + id + " verified (its sealed tests pass): a later red is a regression, not pending work");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("roadmap ledger not updated: " + e.Message);
            }
            Console.WriteLine("G0+G1 passed: " + all.Count + " tests." + (expected.Count > 0 ? " (" + expected.Count + " sealed test(s) red by design, awaiting impl: " +
                              string.Join(", ", expected.Take(5).Select(r => (string)r.Attribute("testName"))) + ")" : ""));
            return true;
        }

        _lastFailedStage = "G1";
        _lastFailedTests.AddRange(failed.Select(r => (string)r.Attribute("testName")));
        if (!AgentShell.IsAgent)
            Console.WriteLine("Nếu do bạn (người) gây ra: " + Attribution.TestHint(_lastFailedTests,
                (ChangedFiles(null, out _) ?? new List<string>()).Where(Attribution.IsCode).ToList()));
        Console.WriteLine("G0+G1 FAILED: " + failed.Count + "/" + all.Count + " tests");
        foreach (var r in failed.Take(15))
        {
            Console.WriteLine("- " + (string)r.Attribute("testName"));
            var message = (string)r.Descendants(ns + "Message").FirstOrDefault() ?? "";
            foreach (var line in message.Split('\n').Where(l => l.Trim().Length > 0).Take(MaxMessageLines))
                Console.WriteLine("    " + line.Trim());
        }
        if (failed.Count > 15)
            Console.WriteLine("  ... and " + (failed.Count - 15) + " more");
        return false;
    }

    private static bool RunUnityTests(string unity, string platform, List<string> assemblies, string categories = null, string label = null)
    {
        string dir = Path.Combine(_root, "Dashboard", "Data");
        Directory.CreateDirectory(dir);
        string name = platform + (label != null ? "-" + label : "");
        string results = Path.Combine(dir, name + ".xml");
        string log = Path.Combine(dir, name + ".log");
        if (File.Exists(results))
            File.Delete(results);

        Console.WriteLine("Running Unity " + name + ": " + string.Join(";", assemblies) + (categories != null ? " [" + categories + "]" : ""));
        var (code, _) = Exec(unity,
            "-batchmode -nographics -projectPath \"" + Path.Combine(_root, "UnityProject") + "\" -runTests -testPlatform " + platform +
            " -assemblyNames \"" + string.Join(";", assemblies) + "\"" +
            (categories != null ? " -testCategory \"" + categories + "\"" : "") +
            " -testResults \"" + results + "\" -logFile \"" + log + "\"");

        if (!File.Exists(results))
        {
            RecordTests("Unity " + name, "failed", 0, 0, 0, null, "Unity did not produce results (exit " + code + ")");
            Console.WriteLine("Unity " + name + " FAILED to run (exit " + code + "). Compile errors from the log:");
            if (File.Exists(log))
                foreach (var line in File.ReadLines(log).Where(l => l.Contains("error CS")).Distinct().Take(20))
                    Console.WriteLine("  " + ShortenPath(line.Trim()));
            return false;
        }

        var doc = XDocument.Load(results);
        var cases = doc.Descendants("test-case").ToList();
        // Inconclusive/Skipped is UNKNOWN, and UNKNOWN never counts as passed.
        var failed = cases.Where(c => (string)c.Attribute("result") == "Failed" || (string)c.Attribute("result") == "Inconclusive").ToList();
        RecordTests("Unity " + name, failed.Count == 0 && cases.Count > 0 ? "passed" : "failed", cases.Count - failed.Count, failed.Count, cases.Count, results,
            categories != null ? "categories " + categories : null);
        if (cases.Count == 0 && categories != null)
        {
            Console.WriteLine("Unity " + name + " UNKNOWN: 0 tests matched " + categories + " (view cases missing or failed to load?)");
            return false;
        }
        if (failed.Count == 0)
        {
            Console.WriteLine("Unity " + name + " passed: " + cases.Count + " tests.");
            return true;
        }

        _lastFailedStage = "unity";
        _lastFailedTests.AddRange(failed.Select(c => (string)c.Attribute("name")));
        Console.WriteLine("Unity " + name + " FAILED: " + failed.Count + "/" + cases.Count);
        foreach (var c in failed.Take(15))
        {
            Console.WriteLine("- " + (string)c.Attribute("fullname"));
            var message = (string)c.Descendants("message").FirstOrDefault() ?? "";
            foreach (var line in message.Split('\n').Where(l => l.Trim().Length > 0).Take(MaxMessageLines))
                Console.WriteLine("    " + line.Trim());
        }
        return false;
    }


    // ------------------------------------------------------------------ check / refs / hooks

    private static int Check(string[] args, string baseRef)
    {
        var explicitPaths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != baseRef).ToList();
        List<RuleViolation> violations;
        string scope;

        if (args.Contains("--all"))
        {
            violations = ArchitectureScanner.ScanAll(_root);
            scope = "whole project";
        }
        else if (args.Contains("--staged"))
        {
            var files = TestGate.AgentControl.GitChanges.Staged(_root);
            violations = ArchitectureScanner.ScanFiles(_root, files);
            scope = files.Count + " staged file(s)";
        }
        else if (explicitPaths.Count > 0)
        {
            violations = ArchitectureScanner.ScanFiles(_root, explicitPaths.Select(Relative));
            scope = explicitPaths.Count + " file(s)";
        }
        else
        {
            var changed = ChangedFiles(baseRef, out _);
            violations = changed == null ? ArchitectureScanner.ScanAll(_root) : ArchitectureScanner.ScanFiles(_root, changed);
            scope = changed == null ? "whole project" : changed.Count + " changed file(s)";
        }

        if (violations.Count == 0)
        {
            Console.WriteLine("check OK (" + scope + ")");
            return 0;
        }
        Console.WriteLine(ArchitectureScanner.Format(violations));
        PrintHumanHints(violations);
        return 1;
    }

    private static int Refs(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            Console.Error.WriteLine("Usage: TestGate refs Assets/_Game/Features/<Feature>");
            return 64;
        }
        folder = Relative(folder).TrimEnd('/');

        var incoming = new AssetRules(_root).IncomingReferences(folder).ToList();
        var asmdefs = AsmdefRules.Load(_root);
        var inside = new HashSet<string>(asmdefs.Where(a => a.Folder.StartsWith(folder + "/", StringComparison.Ordinal) || a.Folder == folder).Select(a => a.Name));
        incoming.AddRange(asmdefs
            .Where(a => !inside.Contains(a.Name))
            .SelectMany(a => a.References.Where(inside.Contains).Select(r => a.Path + " -> assembly " + r)));

        if (incoming.Count == 0)
        {
            Console.WriteLine("No incoming references to " + folder + ": safe to delete (then run the gate).");
            return 0;
        }
        Console.WriteLine("DELETE BLOCKED: " + incoming.Count + " incoming reference(s) to " + folder);
        foreach (var r in incoming.Take(30))
            Console.WriteLine("  " + r);
        return 1;
    }

    /// <summary>
    /// For a PERSON at a terminal (pre-commit, `check`, `run`): what to do if it was a slip, what if intended. Never printed in an
    /// agent's shell (CLAUDECODE=1): an agent following "git checkout -- file" could revert a human's work.
    /// </summary>
    private static void PrintHumanHints(IEnumerable<RuleViolation> violations)
    {
        if (AgentShell.IsAgent)
            return;
        var hints = violations.Select(v => v.RuleId + " " + v.Path + (v.Line > 0 ? ":" + v.Line : "") + "  → " + Attribution.HumanHint(v)).Distinct().Take(15).ToList();
        if (hints.Count == 0)
            return;
        Console.WriteLine("Nếu do bạn (người) gây ra:");
        foreach (var h in hints)
            Console.WriteLine("  " + h);
    }

    private static string ReadStdin()
    {
        // Hook JSON is UTF-8. Console.In uses the console code page on Windows and mangles Vietnamese text
        // (an Edit's old_string would no longer match the file).
        try
        {
            using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    /// <summary>Per-file rules for files an agent just changed (Edit/Write or a Bash command).</summary>
    private static List<string> CheckEditedFiles(IEnumerable<string> files)
    {
        var rel = files.Select(Relative).Where(r => !r.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(r)).ToList();
        var violations = rel.Count == 0 ? new List<RuleViolation>() : ArchitectureScanner.ScanFiles(_root, rel, projectRules: false);
        if (violations.Count == 0)
            return new List<string>();
        return new[] { "Architecture rules violated by your last change:" }.Concat(ArchitectureScanner.Format(violations).Split('\n')).ToList();
    }

    /// <summary>
    /// Stop: the agent may only finish when the gate is green. Skips (ms) if nothing changed since the
    /// last green run. After MaxStopBlocks consecutive blocks it lets the agent stop, having told it to
    /// report the failures (prevents infinite loops).
    /// </summary>
    private static int HookStop()
    {
        var input = HookInput.Parse(ReadStdin());
        Recorder.Use(input);

        // Checks the agent can't argue with: every changed file is in the task's scope, tests weren't weakened,
        // and everything on disk is explained by an observed event (unexplained = UNOBSERVED, shown, not blocking).
        string mergeBase = Exec("git", "rev-parse --verify --quiet origin/main", quiet: true).code == 0
            ? Exec("git", "merge-base origin/main HEAD", quiet: true).output.Trim() : "HEAD";
        var humanFixes = new List<Fix>();
        var agentFindings = AgentHooks.StopChecks(_root, input, path =>
        {
            var (c, o) = Exec("git", "show \"" + mergeBase + ":" + path + "\"", quiet: true);
            return c == 0 ? o : null;
        }, humanFixes);

        string fingerprint = Fingerprint();
        string okFile = Path.Combine(_root, "Dashboard", "Data", "stop-gate-ok.txt");
        string blocksFile = Path.Combine(_root, "Dashboard", "Data", "stop-gate-blocks.txt");
        if (agentFindings.Count == 0 && humanFixes.Count == 0 && File.Exists(okFile) && File.ReadAllText(okFile) == fingerprint)
        {
            Recorder.Emit(Recorder.New(EventTypes.StopAllowed, "testgate").With("reason", "nothing changed since the last green gate"));
            AgentHooks.RecordClaim(input);
            return 0;
        }

        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        int code;
        try
        {
            code = agentFindings.Count > 0 ? 1 : Run(BuildPlan(null, false));
            if (agentFindings.Count > 0)
                foreach (var f in agentFindings)
                    Console.WriteLine(f);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Who caused the red gate? Only the agent's own failures block it; a human's are handed to the human (dashboard,
        // SessionStart, their next `run`/commit) with human commands. Pre-commit and CI still block the merge.
        var agentFixes = new List<Fix>();
        if (code != 0 && agentFindings.Count == 0)
        {
            var authors = Attribution.Authors(EventLog.For(_root).ReadAll());
            var changed = new HashSet<string>(ChangedFiles(null, out _) ?? new List<string>(), StringComparer.Ordinal);
            foreach (var fx in Attribution.Classify(_lastViolations, _lastFailedTests, _lastFailedStage, changed, authors))
                (fx.Who == "human" ? humanFixes : agentFixes).Add(fx);
            if (agentFixes.Count == 0 && humanFixes.Count > 0)
                code = 0; // nothing the agent did is red
        }
        Attribution.Save(_root, humanFixes);
        if (humanFixes.Count > 0)
            Recorder.Emit(Recorder.New(EventTypes.HumanFixNeeded, "testgate").With("count", humanFixes.Count.ToString())
                .With("fixes", string.Join(" | ", humanFixes.Take(5).Select(f => f.Problem.Length > 120 ? f.Problem.Substring(0, 120) + "…" : f.Problem))));

        Directory.CreateDirectory(Path.GetDirectoryName(okFile));
        if (code == 0 && humanFixes.Count > 0)
        {
            File.WriteAllText(blocksFile, "0");
            Recorder.Emit(Recorder.New(EventTypes.StopAllowed, "testgate").With("reason", "only failures caused by a human (" + humanFixes.Count + "): handed to the human"));
            AgentHooks.RecordClaim(input);
            return 0;
        }
        if (code == 0)
        {
            File.WriteAllText(okFile, fingerprint);
            File.WriteAllText(blocksFile, "0");
            Recorder.Emit(Recorder.New(EventTypes.StopAllowed, "testgate").With("reason", "gate green"));
            AgentHooks.RecordClaim(input);
            return 0;
        }
        Recorder.Emit(Recorder.New(EventTypes.StopBlocked, "testgate").With("exit", code.ToString())
            .With("reason", agentFindings.Count > 0 ? string.Join("; ", agentFindings.Take(5)) : "gate red"));
        AgentHooks.RecordClaim(input); // judged together with the block above

        int blocks = (File.Exists(blocksFile) && int.TryParse(File.ReadAllText(blocksFile).Trim(), out var b) ? b : 0) + 1;
        if (blocks > MaxStopBlocks)
        {
            File.WriteAllText(blocksFile, "0");
            return 0; // already told to report; don't loop forever
        }
        File.WriteAllText(blocksFile, blocks.ToString());

        // Only what failed, not the plan header (token-cheap): failure lines of the captured gate output.
        var summary = captured.ToString().Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Where(l => l.StartsWith("ARCH", StringComparison.Ordinal) || l.StartsWith("SCOPE", StringComparison.Ordinal) || l.StartsWith("TESTS", StringComparison.Ordinal)
                        || l.StartsWith("JUNK", StringComparison.Ordinal) || l.StartsWith("STALE", StringComparison.Ordinal) || l.Contains("FAILED") || l.StartsWith("- ", StringComparison.Ordinal)
                        || l.StartsWith("    ", StringComparison.Ordinal) || l.StartsWith("  ", StringComparison.Ordinal) && l.Contains("error") || l.StartsWith("Rule details", StringComparison.Ordinal))
            .ToList();
        Console.Error.WriteLine("You cannot finish yet: the test gate is red (exit " + code + "). Fix this, then finish:");
        foreach (var line in summary.Take(40))
            Console.Error.WriteLine(line);
        if (humanFixes.Count > 0)
            Console.Error.WriteLine("Also failing, NOT yours (a human's edit; don't fix or work around, mention it in your report): " +
                                    string.Join("; ", humanFixes.Take(3).Select(f => f.Problem.Length > 100 ? f.Problem.Substring(0, 100) + "…" : f.Problem)));
        if (blocks == MaxStopBlocks)
            Console.Error.WriteLine("LAST BLOCK: if you cannot fix it, stop and report these failures to the user verbatim.");
        return 2;
    }

    private static string Fingerprint()
    {
        var changed = ChangedFiles(null, out _);
        IEnumerable<string> files = changed ?? new[] { "UnityProject/Assets", "UnityProject/Packages", "UnityProject/ProjectSettings", "Tools" }
            .Select(d => Path.Combine(_root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(Rel);

        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var full = Path.Combine(_root, f);
            var info = new FileInfo(full);
            sb.Append(f).Append('|').Append(info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "deleted").Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Relative(string path) =>
        Path.IsPathRooted(path) ? Rel(path) : path.Replace('\\', '/');

    // ------------------------------------------------------------------ helpers

    private static List<Asmdef> LoadAsmdefs()
    {
        var list = new List<Asmdef>();
        foreach (var path in Directory.GetFiles(Path.Combine(_root, "UnityProject", "Assets"), "*.asmdef", SearchOption.AllDirectories))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var a = new Asmdef
            {
                Name = root.GetProperty("name").GetString(),
                Dir = Rel(Path.GetDirectoryName(path)),
                Pure = root.TryGetProperty("noEngineReferences", out var ne) && ne.GetBoolean(),
            };
            if (root.TryGetProperty("references", out var refs))
                a.References = refs.EnumerateArray().Select(r => r.GetString()).ToList();
            if (root.TryGetProperty("includePlatforms", out var plats))
                a.EditorOnly = plats.EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "Editor" });
            list.Add(a);
        }
        return list;
    }

    private static Asmdef OwnerOf(string file, List<Asmdef> asmdefs) =>
        asmdefs
            .Where(a => file.StartsWith(a.Dir + "/", StringComparison.Ordinal))
            .OrderByDescending(a => a.Dir.Length) // nearest .asmdef wins, like Unity
            .FirstOrDefault();

    private static bool IsUnityAsset(string file)
    {
        string[] unityExtensions = { ".unity", ".prefab", ".asset", ".mat", ".anim", ".controller", ".shader", ".asmref" };
        return unityExtensions.Any(e => file.EndsWith(e, StringComparison.Ordinal))
            || file.StartsWith("UnityProject/Packages/", StringComparison.Ordinal)
            || file.StartsWith("UnityProject/ProjectSettings/", StringComparison.Ordinal)
            || (file.StartsWith("UnityProject/Assets/", StringComparison.Ordinal) && file.EndsWith(".cs", StringComparison.Ordinal)); // .cs without asmdef → Assembly-CSharp
    }

    /// <summary>Returns null (= run everything) when git is unavailable.</summary>
    private static List<string> ChangedFiles(string baseRef, out string fullReason) =>
        TestGate.AgentControl.GitChanges.Files(_root, baseRef, out fullReason);

    private static (int code, string output) Exec(string file, string arguments, bool quiet = false)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var p = Process.Start(psi);
            var stderr = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout + stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception) when (quiet)
        {
            return (127, "");
        }
    }

    private static string FindRepoRoot() => Game.Architecture.Rules.Layout.FindRoot(Directory.GetCurrentDirectory());

    private static string StatePath => Path.Combine(_root, "Dashboard", "Data", "testgate-unity-failures.txt");

    private static int ReadUnityFailures() =>
        File.Exists(StatePath) && int.TryParse(File.ReadAllText(StatePath).Trim(), out var n) ? n : 0;

    private static void WriteUnityFailures(int count)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
        File.WriteAllText(StatePath, count.ToString());
    }

    private static string ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Rel(string path) =>
        Path.GetRelativePath(_root, path).Replace('\\', '/');

    private static string ShortenPath(string line) => line.Replace(_root.Replace('\\', '/') + "/", "").Replace(_root + Path.DirectorySeparatorChar, "");

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }
}
