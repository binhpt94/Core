using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    public sealed class PlanEditDecision
    {
        public const string Ask = "ask";
        public const string Deny = "deny";
        public string Kind { get; set; }
        public string Reason { get; set; }
        public string ProposedHash { get; set; }
    }

    public sealed class PlanDraftReport
    {
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Changes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Docs/Plan/features.md stays the owner's, but an AI may write it like this:
    ///   PreToolUse   Edit/Write only. The text it WOULD write is checked (ARCH110); invalid → blocked with the errors, never asked.
    ///                Valid → "ask": Claude Code shows the owner a yes/no with every feature/requirement change.
    ///   PostToolUse  the tool ran (= the owner said yes) and the file is exactly what was asked → PLAN_EDIT_APPROVED (content hash).
    ///   Scope/Stop   a features.md change is in scope only while the file is exactly an approved content.
    /// One yes = one write: the next write asks again; Bash/scripts/MCP never ask, so they stay a breach.
    /// </summary>
    public static class PlanEditGate
    {
        public const string AskedEvent = "PLAN_EDIT_ASKED";
        public const string ApprovedEvent = "PLAN_EDIT_APPROVED";
        private const int MaxLines = 40;

        public static bool IsPlan(string rel) => rel == PlanParser.RelativePath;

        public static PlanEditDecision Decide(string root, HookInput h)
        {
            string current = Current(root);
            if (h.ToolName != "Edit" && h.ToolName != "Write")
                return Denied(PlanParser.RelativePath + ": write it with Edit or Write, so the owner is asked yes/no for this exact change (" + h.ToolName + " is not)");
            string proposed = Proposed(current, h, out string error);
            if (proposed == null)
                return Denied(error);
            var report = Draft(root, proposed);
            if (report.Errors.Count > 0)
                return Denied("the plan would be invalid, so the owner is not asked. Fix the draft (./Tools/test-gate.sh roadmap draft <file>):\n" +
                              string.Join("\n", report.Errors.Take(MaxLines)));
            var lines = report.Changes.Count == 0 ? new List<string> { "(không đổi feature/requirement nào: chỉ mô tả hoặc định dạng)" } : report.Changes;
            return new PlanEditDecision
            {
                Kind = PlanEditDecision.Ask,
                ProposedHash = Hash(proposed),
                Reason = "Ghi " + PlanParser.RelativePath + "? Gate đã kiểm tra: hợp lệ (ARCH110). Thay đổi:\n" +
                         string.Join("\n", lines.Take(MaxLines)) + (lines.Count > MaxLines ? "\n… +" + (lines.Count - MaxLines) + " dòng" : "")
            };
        }

        /// <summary>The JSON Claude Code reads from a PreToolUse hook to show the owner a yes/no.</summary>
        public static string AskJson(PlanEditDecision d) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hookSpecificOutput"] = new Dictionary<string, string>
            {
                ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "ask", ["permissionDecisionReason"] = d.Reason
            }
        });

        /// <summary>A draft (whole file text) checked like the real file, with its changes against the current plan.</summary>
        public static PlanDraftReport Draft(string root, string draft)
        {
            var r = new PlanDraftReport();
            r.Errors.AddRange(PlanRules.CheckText(root, draft).Select(v => "ARCH110 line " + v.Line + ": " + v.Message));
            if (r.Errors.Count == 0)
                r.Changes = Diff(PlanParser.Parse(Current(root)), PlanParser.Parse(draft));
            return r;
        }

        /// <summary>After the tool ran: the owner said yes. Records the approval only for the exact text that was asked.</summary>
        public static void AfterWrite(string root, HookInput h)
        {
            var asked = EventLog.For(root).ReadAll().LastOrDefault(e => e.Type == AskedEvent && e.Get("toolUseId") == h.ToolUseId);
            string full = Path.Combine(root, PlanParser.RelativePath);
            if (asked == null || !File.Exists(full) || asked.Get("proposed") != Hash(File.ReadAllText(full)))
                return;
            Recorder.Emit(Recorder.New(ApprovedEvent, "hook").With("path", PlanParser.RelativePath).With("toolUseId", h.ToolUseId)
                .With("proposed", asked.Get("proposed")).With("after", WorkSnapshot.HashFile(full)));
        }

        /// <summary>Is this features.md content (snapshot hash) one the owner approved?</summary>
        public static bool IsApproved(string root, string rel, string state) =>
            IsPlan(rel) && EventLog.For(root).ReadAll().Any(e => e.Type == ApprovedEvent && e.Get("after") == state);

        public static string Proposed(string current, HookInput h, out string error)
        {
            error = null;
            if (h.ToolName == "Write")
                return h.Input("content") ?? "";
            string text = Norm(current), oldText = Norm(h.Input("old_string") ?? ""), newText = Norm(h.Input("new_string") ?? "");
            if (oldText.Length == 0)
            {
                error = "Edit without old_string";
                return null;
            }
            int count = Count(text, oldText);
            bool all = (h.Input("replace_all") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            if (count == 0 || count > 1 && !all)
            {
                error = PlanParser.RelativePath + ": old_string " + (count == 0 ? "not found" : "is not unique (" + count + "×)");
                return null;
            }
            if (all)
                return text.Replace(oldText, newText);
            int i = text.IndexOf(oldText, StringComparison.Ordinal);
            return text.Substring(0, i) + newText + text.Substring(i + oldText.Length);
        }

        public static List<string> Diff(Plan before, Plan after)
        {
            var o = new List<string>();
            before = before ?? new Plan();
            foreach (var f in after.Features)
            {
                var b = before.Feature(f.Id);
                if (b == null)
                    o.Add("+ " + f.Id + "  " + f.Title + "  " + Tags(f));
                else
                {
                    var changed = new List<string>();
                    if (b.Title != f.Title) changed.Add("tên \"" + b.Title + "\" → \"" + f.Title + "\"");
                    void Field(string name, string x, string y) { if (x != y) changed.Add(name + (x.Length == 0 ? "" : " " + x) + " → " + (y.Length == 0 ? "(bỏ)" : y)); }
                    Field("ưu tiên", b.Priority, f.Priority);
                    Field("modules", string.Join(", ", b.Modules), string.Join(", ", f.Modules));
                    Field("tag", string.Join(" ", b.Tags.OrderBy(t => t, StringComparer.Ordinal)), string.Join(" ", f.Tags.OrderBy(t => t, StringComparer.Ordinal)));
                    Field("slots", string.Join(", ", b.Slots), string.Join(", ", f.Slots));
                    Field("art", b.ArtLevel ?? "", f.ArtLevel ?? "");
                    if (Deps(b) != Deps(f)) changed.Add("deps \"" + Deps(b) + "\" → \"" + Deps(f) + "\"");
                    if (string.Join("\n", b.Description) != string.Join("\n", f.Description)) changed.Add("mô tả đổi");
                    if (changed.Count > 0)
                        o.Add("~ " + f.Id + ": " + string.Join(" · ", changed));
                }
            }
            foreach (var b in before.Features.Where(b => after.Feature(b.Id) == null))
                o.Add("- " + b.Id + "  " + b.Title + " (bỏ cả " + b.Requirements.Count + " requirement)");
            foreach (var r in after.AllRequirements)
            {
                var b = before.Requirement(r.Id);
                if (b == null)
                    o.Add("+ " + r.Id + " (" + r.Feature + ")  " + Req(r));
                else if (b.Text != r.Text || ReqTags(b) != ReqTags(r) || b.Feature != r.Feature)
                    o.Add("~ " + r.Id + " (" + r.Feature + "): \"" + Req(b) + "\" → \"" + Req(r) + "\"" + (b.Feature != r.Feature ? " (chuyển từ " + b.Feature + ")" : ""));
            }
            foreach (var b in before.AllRequirements.Where(b => after.Requirement(b.Id) == null && after.Feature(b.Feature) != null))
                o.Add("- " + b.Id + " (" + b.Feature + ")  " + Req(b));
            return o;
        }

        private static string Tags(PlanFeature f) =>
            "[" + f.Priority + "]" + (f.Modules.Count > 0 ? " [modules: " + string.Join(", ", f.Modules) + "]" : "") +
            string.Concat(f.Tags.OrderBy(t => t, StringComparer.Ordinal).Select(t => " [" + t + "]")) +
            (f.Slots.Count > 0 ? " [slots: " + string.Join(", ", f.Slots) + "]" : "") + (f.ArtLevel != null ? " [art: " + f.ArtLevel + "]" : "");

        private static string Deps(PlanFeature f) =>
            string.Join(" · ", f.Dependencies.Select(d => d.Kind.ToString().ToLowerInvariant() + " " + d.Feature + (d.Contract != null ? " (" + d.Contract + ")" : "")));

        private static string ReqTags(PlanRequirement r) => string.Concat(r.Tags.OrderBy(t => t, StringComparer.Ordinal).Select(t => "  [" + t + "]"));

        private static string Req(PlanRequirement r) => r.Text + ReqTags(r);

        private static string Current(string root)
        {
            string full = Path.Combine(root, PlanParser.RelativePath);
            return File.Exists(full) ? File.ReadAllText(full) : "";
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static string Norm(string s) => (s ?? "").Replace("\r\n", "\n");

        /// <summary>Content hash independent of line endings (the Edit tool may write CRLF).</summary>
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Norm(text))).Take(12).Select(b => b.ToString("x2")));
        }

        private static PlanEditDecision Denied(string reason) => new PlanEditDecision { Kind = PlanEditDecision.Deny, Reason = reason };
    }
}
