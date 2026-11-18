using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using TestGate.AgentControl;
using TestGate.Files;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>
    /// The dashboard data is the CURRENT state: its size must not grow with the history, and building it must stay fast.
    /// The view (tools/dashboard/app.js) may only read fields the data really has.
    /// </summary>
    public sealed class SnapshotTests
    {
        /// <summary>A realistic log: n events over n/100 sessions (edits, commands, gates, claims, some violations).</summary>
        private static List<AgentEvent> Log(int n)
        {
            var list = new List<AgentEvent>(n);
            for (int i = 0; i < n; i++)
            {
                string session = "sess" + (i / 100).ToString("D5");
                int k = i % 100;
                var e = new AgentEvent { Seq = i + 1, Ts = new DateTime(2026, 1, 1).AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ssZ"), Session = session, Agent = "main", Source = "hook", Task = "T" + (i / 300) };
                if (k == 0) e.Type = EventTypes.SessionStarted;
                else if (k == 97) { e.Type = EventTypes.GateFinished; e.Data["result"] = i % 700 == 97 ? "failed" : "passed"; }
                else if (k == 98) { e.Type = EventTypes.Claim; e.Data["text"] = "Đã xong việc số " + i + ", mọi test đều xanh."; }
                else if (k == 99) e.Type = EventTypes.StopAllowed;
                else if (k % 13 == 0) { e.Type = EventTypes.PolicyDenied; e.Data["tool"] = "Write"; e.Data["target"] = "Assets/_Game/Features/B/Logic/F" + i + ".cs"; e.Data["reason"] = "outside task"; }
                else if (k % 3 == 0) { e.Type = EventTypes.CommandRun; e.Data["command"] = "dotnet build LogicTests && ./tools/test-gate.sh check --files Assets/_Game/Features/A/Logic/X" + i + ".cs"; }
                else
                {
                    e.Type = EventTypes.FileModified;
                    e.Data["path"] = "Assets/_Game/Features/F" + (i % 40) + "/Logic/File" + (i % 2000) + ".cs";
                    e.Data["via"] = "Edit";
                    e.Data["inScope"] = k % 29 == 0 ? "false" : "true";
                    e.Data["scope"] = "Features/F" + (i % 40);
                }
                list.Add(e);
            }
            return list;
        }

        /// <summary>Benchmark helper (run by hand): writes a valid hash-chained log of BENCH_EVENTS events to BENCH_ROOT/.agent/.
        ///   BENCH_ROOT=/path/copy BENCH_EVENTS=50000 dotnet test LogicTests --filter WriteBenchmarkLog; then time `test-gate.sh dash` there.</summary>
        [Test, Explicit("benchmark helper")]
        public void WriteBenchmarkLog()
        {
            string root = Environment.GetEnvironmentVariable("BENCH_ROOT");
            Assume.That(root, Is.Not.Null);
            int n = int.TryParse(Environment.GetEnvironmentVariable("BENCH_EVENTS"), out var x) ? x : 50_000;
            var sb = new System.Text.StringBuilder();
            string prev = EventLog.Genesis;
            foreach (var e in Log(n))
            {
                e.Prev = prev;
                e.Hash = EventLog.ComputeHash(e);
                prev = e.Hash;
                sb.Append(EventLog.Serialize(e)).Append('\n');
            }
            Directory.CreateDirectory(Path.Combine(root, ".agent"));
            File.WriteAllText(Path.Combine(root, ".agent", "events.jsonl"), sb.ToString());
            Assert.That(EventLog.For(root).Verify().Ok, Is.True);
        }

        private static int Size(List<AgentEvent> log) =>
            AgentCli.ToJson(SnapshotBuilder.Build(log, new ChainStatus { Ok = true, Count = log.Count }, null, "now", "p")).Length;

        [Test]
        public void TheSnapshotDoesNotGrowWithHistory_AndBuildsFast()
        {
            int small = Size(Log(5_000));
            var big = Log(50_000);
            var watch = Stopwatch.StartNew();
            int large = Size(big);
            watch.Stop();

            TestContext.WriteLine("5k events: " + small / 1024 + " KB · 50k events: " + large / 1024 + " KB in " + watch.ElapsedMilliseconds + " ms");
            Assert.That(large, Is.LessThanOrEqualTo(60 * 1024), "event-dependent part of data.json (roadmap/files depend on the project, not on time)");
            Assert.That(Math.Abs(large - small), Is.LessThan(small / 10), "10x the history must not make it bigger");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1000));
        }

        [Test]
        public void OneSessionsDetail_IsOnlyThatSession()
        {
            var log = Log(1_000);
            var d = SnapshotBuilder.Detail(log, "sess00003");
            Assert.That(d.Steps.Count, Is.InRange(1, 100));
            Assert.That(d.Lanes.All(l => l.Id.StartsWith("sess00003", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void SessionsAreSummarised_NewestFirst_Capped()
        {
            var s = SnapshotBuilder.Build(Log(10_000), new ChainStatus { Ok = true }, null, "now", "p");
            Assert.That(s.Counts["sessions"], Is.EqualTo(100));
            Assert.That(s.Sessions.Count, Is.EqualTo(Snapshot.MaxSessions));
            Assert.That(s.Sessions[0].Id, Is.EqualTo("sess00099"));
            Assert.That(s.Sessions[0].Verdict, Is.Not.Null);
            Assert.That(s.Anomalies.Recent.Count, Is.EqualTo(Snapshot.MaxRecent));
            Assert.That(s.Anomalies.Recent[0].Seq, Is.GreaterThan(s.Anomalies.Recent[1].Seq), "newest first");
        }

        // ------------------------------------------------------------------ view ↔ data contract

        private static string AppJs()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "tools", "dashboard", "app.js");
                if (File.Exists(p))
                    return File.ReadAllText(p);
            }
            Assert.Fail("tools/dashboard/app.js not found");
            return null;
        }

        private static JsonElement Shape(object o) => JsonDocument.Parse(AgentCli.ToJson(o)).RootElement;

        private static IEnumerable<string> Missing(string js, string variable, JsonElement data)
        {
            foreach (Match m in Regex.Matches(js, @"\b" + variable + @"\.([A-Za-z_]\w*)(?:\.([A-Za-z_]\w*))?"))
            {
                string a = m.Groups[1].Value, b = m.Groups[2].Success ? m.Groups[2].Value : null;
                if (!data.TryGetProperty(a, out var child))
                    yield return variable + "." + a;
                else if (b != null && child.ValueKind == JsonValueKind.Object && !child.TryGetProperty(b, out _))
                    yield return variable + "." + a + "." + b;
            }
        }

        [Test]
        public void TheViewReadsOnlyFieldsTheDataHas()
        {
            var task = ScopePolicy.Start("T1", "t", new[] { "A" }, new string[0], false, "now");
            var s = SnapshotBuilder.Build(Log(300), new ChainStatus { Ok = true }, task, "now", "p");
            s.Roadmap = new RoadmapView();
            s.Housekeeping = new FilesView();
            var d = SnapshotBuilder.Detail(Log(300), "sess00001");
            string js = Regex.Replace(AppJs(), @"^\s*//.*$", "", RegexOptions.Multiline); // comments aren't reads

            var missing = Missing(js, "M", Shape(s)).Concat(Missing(js, "D", Shape(d))).Distinct().ToList();
            Assert.That(missing, Is.Empty, "app.js reads fields that data.json doesn't have (renamed in C#?)");
            Assert.That(Regex.Matches(js, @"\bM\.\w+").Count, Is.GreaterThan(10), "the check is really looking at the code");
        }
    }
}
