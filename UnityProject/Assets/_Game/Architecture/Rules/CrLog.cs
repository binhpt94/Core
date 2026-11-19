using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>One change request between modules (Docs/Plan/CRs/&lt;target&gt;.txt), folded from its log lines.</summary>
    public sealed class CrEntry
    {
        public string Id { get; set; }
        public string From { get; set; }      // feature (F-ID) or module of the task that asked
        public string To { get; set; }        // target plan feature (F-ID), or "CORE"
        public string Module { get; set; }    // target code folder (Features/<Module>), "Core" for Core/Runtime
        public string Kind { get; set; }      // additive | breaking | meaning | core-additive | core-breaking | core-meaning
        public string Text { get; set; }
        public List<string> Api { get; } = new List<string>();  // PublicApi lines the CR adds ("- line" = removes)
        public string Session { get; set; }
        public string Task { get; set; }
        public string Patch { get; set; }
        public string Status { get; set; } = CrLog.Open;
        public string DecidedBy { get; set; }
        public string Reason { get; set; }
        public string FiledTs { get; set; }
        public List<string> History { get; } = new List<string>();

        /// <summary>Approved CRs are requirements of their target feature (tests [Covers("CR-n")], seals, lanes: like any R-).</summary>
        public bool IsRequirement => (Status == CrLog.Approved || Status == CrLog.Delivered) && To != CrLog.CoreTarget;
        public bool Closed => Status == CrLog.Rejected || Status == CrLog.Withdrawn;
    }

    /// <summary>
    /// Docs/Plan/CRs/&lt;target&gt;.txt: append-only, written only by the gate (`task need`, `cr …`). One line per event:
    ///   file CR-n from=F-A to=F-B module=M kind=K task=T session=S ts=t [api=&lt;base64 lines&gt;] [patch=&lt;file&gt;] reason=&lt;what and why&gt;
    ///   approve|reject|ask|withdraw|deliver CR-n by=&lt;gate|arbiter:S|owner|S&gt; ts=t reason=&lt;why&gt;
    /// A module never asks another one by chat: what it needs from B is a CR, and B's work on it is traced to it.
    /// </summary>
    public static class CrLog
    {
        public const string Dir = "Docs/Plan/CRs";
        public const string CoreTarget = "CORE";
        public const string Open = "open", Approved = "approved", Rejected = "rejected", Asked = "ask", Withdrawn = "withdrawn", Delivered = "delivered";
        private static readonly Regex Field = new Regex(@"(\w+)=(\S+)");

        public static string FileOf(string target) => Dir + "/" + target + ".txt";

        public static Dictionary<string, CrEntry> Load(string repoRoot)
        {
            var crs = new Dictionary<string, CrEntry>(StringComparer.Ordinal);
            string dir = Path.Combine(repoRoot ?? "", Dir);
            if (repoRoot == null || !Directory.Exists(dir))
                return crs;
            foreach (var file in Directory.GetFiles(dir, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
                foreach (var line in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
                    Apply(crs, line);
            return crs;
        }

        public static void Apply(Dictionary<string, CrEntry> crs, string line)
        {
            var parts = line.Trim().Split(new[] { ' ' }, 3);
            if (parts.Length < 2 || !parts[1].StartsWith("CR-", StringComparison.Ordinal))
                return;
            string verb = parts[0], id = parts[1], rest = parts.Length > 2 ? parts[2] : "";
            int r = rest.IndexOf("reason=", StringComparison.Ordinal);
            string reason = r >= 0 ? rest.Substring(r + 7).Trim() : null;
            var f = Field.Matches(r >= 0 ? rest.Substring(0, r) : rest).Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
            string Get(string k) => f.TryGetValue(k, out var v) ? v : null;
            if (!crs.TryGetValue(id, out var cr))
            {
                if (verb != "file")
                    return; // a decision about a CR that was never filed: ignored (the gate never writes one)
                crs[id] = cr = new CrEntry { Id = id };
            }
            cr.History.Add(line.Trim());
            switch (verb)
            {
                case "file":
                    cr.From = Get("from"); cr.To = Get("to"); cr.Module = Get("module"); cr.Kind = Get("kind"); cr.Task = Get("task");
                    cr.Session = Get("session"); cr.FiledTs = Get("ts"); cr.Patch = Get("patch"); cr.Text = reason ?? "";
                    cr.Api.Clear();
                    cr.Api.AddRange(DecodeApi(Get("api")));
                    break;
                case "approve": cr.Status = Approved; cr.DecidedBy = Get("by"); cr.Reason = reason; break;
                case "reject": cr.Status = Rejected; cr.DecidedBy = Get("by"); cr.Reason = reason; break;
                case "ask": cr.Status = Asked; cr.DecidedBy = Get("by"); cr.Reason = reason; break;
                case "withdraw": cr.Status = Withdrawn; cr.DecidedBy = Get("by"); cr.Reason = reason; break;
                case "deliver": cr.Status = Delivered; cr.DecidedBy = Get("by"); cr.Reason = reason; break;
            }
        }

        public static string EncodeApi(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            return list.Count == 0 ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", list)));
        }

        public static IEnumerable<string> DecodeApi(string b64)
        {
            if (string.IsNullOrEmpty(b64))
                return Enumerable.Empty<string>();
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\n').Where(l => l.Trim().Length > 0).ToList(); }
            catch (FormatException) { return Enumerable.Empty<string>(); }
        }

        /// <summary>Approved CRs join their target feature as requirements (tag [cr]); the plan file itself is not touched.</summary>
        public static void AddTo(Plan plan, string repoRoot)
        {
            if (plan == null)
                return;
            foreach (var cr in Load(repoRoot).Values.Where(c => c.IsRequirement))
            {
                var target = plan.Feature(cr.To);
                if (target == null || plan.Requirement(cr.Id) != null)
                    continue;
                var req = new PlanRequirement { Id = cr.Id, Feature = target.Id, Text = cr.Text + " (CR từ " + cr.From + ")", Line = 0 };
                req.Tags.Add("cr");
                target.Requirements.Add(req);
            }
        }
    }
}
