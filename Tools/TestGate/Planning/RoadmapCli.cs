using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>
    ///   roadmap status [F-ID]     features with computed status (+ requirements of one feature)
    ///   roadmap gaps              everything missing, most severe first (agent work vs human decisions)
    ///   roadmap next              what to do now, in parallel lanes, with the exact `task start` command
    ///   roadmap propose "text" [--feature F-ID]   agent → Docs/Plan/Inbox/ (the human decides)
    ///   roadmap accept R-ID       OWNER (terminal, or yes/no in Claude Code): accept a [manual] requirement / a reworded one
    ///   roadmap draft &lt;file&gt;      check a proposed features.md and list its feature/requirement changes
    /// </summary>
    public static class RoadmapCli
    {
        public static PlanReport Report(string root) => PlanStatus.Compute(PlanInputs.Load(root));

        public static int Run(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "status";
            if (sub == "draft")
                return Draft(root, args.Length > 2 ? args[2] : null);
            if (PlanParser.Load(root) == null)
            {
                Console.WriteLine("No " + PlanParser.RelativePath + " yet. The human writes it (syntax: Docs/PLANNING.md).");
                return sub == "status" ? 0 : 1;
            }
            switch (sub)
            {
                case "gaps": return Gaps(root);
                case "next": return Next(root);
                case "propose": return Propose(root, args);
                case "accept": return Accept(root, args.Length > 2 ? args[2] : null, args);
                default: return Status(root, args.Length > 2 ? args[2] : null);
            }
        }

        /// <summary>
        /// `roadmap draft &lt;file&gt;`: a whole proposed features.md (e.g. .scratch/features.draft.md) checked like the real one,
        /// with every feature/requirement change. Show this to the owner; when agreed, write features.md (the owner is asked yes/no).
        /// </summary>
        private static int Draft(string root, string file)
        {
            if (file == null || !File.Exists(Path.Combine(root, file)) && !File.Exists(file))
            {
                Console.Error.WriteLine("usage: roadmap draft <file>   (a whole proposed " + PlanParser.RelativePath + ", e.g. .scratch/features.draft.md)");
                return 2;
            }
            var r = PlanEditGate.Draft(root, File.ReadAllText(File.Exists(file) ? file : Path.Combine(root, file)));
            foreach (var e in r.Errors)
                Console.WriteLine("ERROR " + e);
            if (r.Errors.Count > 0)
            {
                Console.WriteLine("Draft is invalid: fix it before asking the owner.");
                return 1;
            }
            Console.WriteLine("Draft OK (ARCH110). Changes against " + PlanParser.RelativePath + ":");
            foreach (var c in r.Changes.DefaultIfEmpty("(không đổi feature/requirement nào)"))
                Console.WriteLine("  " + c);
            return 0;
        }

        private static int Status(string root, string feature)
        {
            var r = Report(root);
            foreach (var e in r.Errors)
                Console.WriteLine("PLAN ERROR " + e);
            if (feature == null)
            {
                Console.WriteLine((r.Title ?? "Plan") + " · test results: " + (r.ResultsUtc ?? "none (run the gate)"));
                Console.WriteLine(Pad("FEATURE", 16) + Pad("STATUS", 12) + Pad("P", 4) + Pad("REQ", 7) + Pad("STORY", 6) + Pad("ART", 10) + "TITLE");
                foreach (var f in r.Features)
                    Console.WriteLine(Pad(f.Id, 16) + Pad(f.Status, 12) + Pad(f.Priority, 4) + Pad(f.Verified + "/" + f.Total, 7) +
                                      Pad(f.StoryTotal > 0 ? "S" + f.StoryVerified + "/" + f.StoryTotal : "-", 6) + Pad(f.ArtLevel ?? "-", 10) + f.Title +
                                      (f.Tags.Count > 0 ? " [" + string.Join(",", f.Tags) + "]" : "") +
                                      (f.Debt.Count > 0 ? " · nợ " + f.Debt.Count : "") + (f.ArtDebt.Count > 0 ? " · nợ art " + f.ArtDebt.Count : "") + (f.BlockedBy.Count > 0 ? " · chờ: " + string.Join("; ", f.BlockedBy) : ""));
                int agent = r.Gaps.Count(g => g.Actor == "agent"), human = r.Gaps.Count(g => g.Actor == "human");
                Console.WriteLine("gaps: " + agent + " việc cho agent, " + human + " chờ người quyết · `roadmap gaps` · `roadmap next`");
                return 0;
            }
            var fs = r.Feature(feature);
            if (fs == null)
            {
                Console.Error.WriteLine("No feature " + feature);
                return 1;
            }
            Console.WriteLine(fs.Id + " " + fs.Title + " · " + fs.Status + " · " + fs.Progress);
            foreach (var d in fs.Dependencies)
                Console.WriteLine("  dep " + d.Kind + " " + d.To + (d.Contract != null ? " (" + d.Contract + (d.Frozen ? ", frozen" : ", NOT frozen") + ")" : "") + (d.Satisfied ? " ✓" : " ✗"));
            foreach (var d in fs.Debt)
                Console.WriteLine("  NỢ " + d);
            if (fs.Slots.Count > 0)
                Console.WriteLine("  art: " + string.Join(", ", fs.Slots) + " · mức " + (fs.ArtLevel ?? "-") + (fs.ArtRequired != null ? " (cần " + fs.ArtRequired + ")" : ""));
            foreach (var d in fs.ArtDebt)
                Console.WriteLine("  NỢ ART " + d);
            foreach (var q in fs.Requirements)
                Console.WriteLine("  " + Pad(q.Status, 13) + Pad(q.Id, 20) + q.Text + "\n" + new string(' ', 15) + q.Detail +
                                  (q.Tests.Count > 0 ? " · tests: " + string.Join(", ", q.Tests.Take(4)) : ""));
            return 0;
        }

        private static int Gaps(string root)
        {
            var r = Report(root);
            if (r.Gaps.Count == 0)
            {
                Console.WriteLine("No gaps: every active requirement is verified and no debt is open.");
                return 0;
            }
            foreach (var g in r.Gaps.Take(60))
                Console.WriteLine((g.Actor == "human" ? "[người] " : g.Actor == "art" ? "[art]   " : "[agent] ") + g);
            if (r.Gaps.Count > 60)
                Console.WriteLine("... and " + (r.Gaps.Count - 60) + " more");
            return 0;
        }

        private static int Next(string root)
        {
            var r = Report(root);
            var items = PlanScheduler.Next(r, TaskRegistry.Load(root).Where(t => t.Status != AgentTask.Closed));
            if (items.Count == 0)
            {
                Console.WriteLine("Nothing for an agent: " + (r.Gaps.Count == 0 ? "no gaps." : "remaining gaps need a human (roadmap gaps)."));
                return 0;
            }
            Console.WriteLine("SONG SONG ĐƯỢC NGAY (module không trùng):");
            foreach (var i in items.Where(x => x.Lane == "now"))
                Print(i, true);
            var later = items.Where(x => x.Lane.StartsWith("after", StringComparison.Ordinal)).ToList();
            if (later.Count > 0)
            {
                Console.WriteLine("SAU ĐÓ:");
                foreach (var i in later)
                    Print(i, false);
            }
            var art = r.Gaps.Where(g => g.Actor == "art").ToList();
            if (art.Count > 0)
            {
                Console.WriteLine("LÀN ART (người vẽ / người duyệt, không giao cho agent):");
                foreach (var g in art)
                    Console.WriteLine("  " + g.Feature + " " + g.Detail);
            }
            var blocked = items.Where(x => x.Lane == "blocked").ToList();
            if (blocked.Count > 0)
            {
                Console.WriteLine("BỊ CHẶN:");
                foreach (var i in blocked)
                    Console.WriteLine("  " + i.Feature + " " + i.Title + ": " + string.Join("; ", i.BlockedBy));
            }
            return 0;
        }

        private static void Print(WorkItem i, bool now)
        {
            Console.WriteLine("  " + i.Priority + " " + i.Feature + " " + i.Title + (i.Rush ? " [RUSH]" : "") + " · modules " + string.Join(",", i.Modules) + (now ? "" : " · " + i.Lane));
            Console.WriteLine("     " + string.Join(" · ", i.Why.Take(5)));
            if (now)
                Console.WriteLine("     " + i.Command);
        }

        private static int Propose(string root, string[] args)
        {
            string text = args.Skip(2).Where((a, k) => !a.StartsWith("--", StringComparison.Ordinal) && (k == 0 || args[k + 1] != "--feature")).FirstOrDefault();
            int fi = Array.IndexOf(args, "--feature");
            string feature = fi >= 0 && fi + 1 < args.Length ? args[fi + 1] : "(new feature)";
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Usage: roadmap propose \"<what and why>\" [--feature F-ID]");
                return 64;
            }
            string slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + (slug.Length > 40 ? slug.Substring(0, 40) : slug) + ".md";
            string path = Path.Combine(root, PlanParser.InboxFolder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "# Đề xuất cho " + feature + "\n\n" + text + "\n\n<!-- Người quyết: chép vào Docs/Plan/features.md (hoặc bỏ), rồi xóa file này. -->\n", Encoding.UTF8);
            Recorder.Emit(Recorder.New(EventTypes.PlanProposed, "roadmap-cli").With("feature", feature).With("file", PlanParser.InboxFolder + name).With("text", AgentHooks.Trim(text, 300)));
            Console.WriteLine("Proposal written: " + PlanParser.InboxFolder + name + " (the plan is unchanged until the human decides)");
            return 0;
        }

        /// <summary>What `roadmap accept` would record (terminal and the owner's yes/no dialog).</summary>
        public static (string text, string error) DescribeAccept(string root, string id)
        {
            var plan = PlanParser.Load(root);
            var req = id == null || plan == null ? null : plan.Requirement(id);
            if (req == null)
                return (null, "unknown requirement '" + id + "'");
            return ("Accept " + req.Id + (req.IsManual ? " [manual]" : "") + " (checked it yourself?)\n  " + req.Text, null);
        }

        private static int Accept(string root, string id, string[] args)
        {
            var plan = PlanParser.Load(root);
            var req = id == null ? null : plan.Requirement(id);
            if (req == null)
            {
                Console.Error.WriteLine("Usage: roadmap accept R-ID (unknown requirement '" + id + "')");
                return 64;
            }
            var auth = OwnerAsk.Authorize(root, args, "roadmap-cli", "accepting a requirement is the owner's decision.");
            if (auth == null)
                return 3;
            Console.WriteLine(req.Id + "  " + req.Text + (req.IsManual ? "  [manual]" : ""));
            if (auth.Human)
            {
                Console.Write("Checked it yourself? Type the id to accept: ");
                if ((Console.ReadLine() ?? "").Trim() != req.Id)
                {
                    Console.WriteLine("Not accepted.");
                    return 1;
                }
            }
            var covers = PlanRules.Covers(root);
            PlanLedger.RecordHuman(root, req, covers);
            if (req.IsManual)
            {
                string p = Path.Combine(root, PlanParser.AcceptedPath);
                var lines = File.Exists(p) ? File.ReadAllLines(p).Where(l => !l.StartsWith(req.Id + " ", StringComparison.Ordinal)).ToList() : new System.Collections.Generic.List<string> { "# Requirements accepted by a human: <id> <requirement hash> <date>. Written by `roadmap accept`." };
                lines.Add(req.Id + " " + req.Hash + " " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                File.WriteAllLines(p, lines);
            }
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(EventTypes.RequirementAccepted, auth.Source).With("requirement", req.Id).With("hash", req.Hash).With("toolUseId", auth.ToolUseId));
            Console.WriteLine("Accepted " + req.Id + ".");
            return 0;
        }

        private static string Pad(string s, int n) => (s ?? "").Length >= n ? s + " " : (s ?? "").PadRight(n);
    }
}
