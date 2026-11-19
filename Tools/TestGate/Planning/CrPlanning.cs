using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Architecture.Rules;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>A change request as the roadmap and the dashboard show it.</summary>
    public sealed class CrView
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Kind { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Text { get; set; }
        public string Decides { get; set; }   // gate | arbiter | owner (who must answer while it is open)
        public double AgeHours { get; set; }
    }

    /// <summary>
    /// What CRs mean for the plan (approved CRs are already requirements of their target: CrLog.AddTo):
    ///   the asking feature owes every CR it filed until the target delivers it (debt: never DONE on a fake), nobody waits idle;
    ///   open CRs are a gap for whoever decides them; a CR's work runs at the priority of the feature waiting for it.
    /// </summary>
    public static class CrPlanning
    {
        public const double OverdueHours = 24;

        public static IEnumerable<string> Debt(PlanReport report, Dictionary<string, CrEntry> crs, string feature)
        {
            foreach (var c in crs.Values.Where(c => c.From == feature && c.Status != CrLog.Withdrawn && c.Status != CrLog.Delivered).OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                if (c.Status == CrLog.Open || c.Status == CrLog.Asked)
                    yield return "chờ duyệt " + c.Id + " → " + c.To + " (" + c.Kind + ", người quyết: " + CrCli.WhoDecides(c) + "): " + c.Text;
                else if (c.Status == CrLog.Rejected)
                    yield return c.Id + " → " + c.To + " bị từ chối (" + (c.Reason ?? "-") + "): đổi cách làm rồi `cr withdraw " + c.Id + "`, hoặc mở CR khác";
                else if (c.To == CrLog.CoreTarget)
                    yield return "chờ Core làm " + c.Id + " (task core riêng, `cr deliver`): " + c.Text;
                else
                {
                    var r = report.Requirement(c.Id);
                    if (r == null || !ReqStatus.IsDone(r.Status))
                        yield return "chờ " + c.To + " làm xong " + c.Id + " (" + (r?.Status ?? "?") + "): đang làm trên bản giả, còn nợ tới khi test của " + c.Id + " xanh";
                }
            }
        }

        public static IEnumerable<Gap> Gaps(Dictionary<string, CrEntry> crs, DateTime now)
        {
            foreach (var c in crs.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                string feature = c.To == CrLog.CoreTarget ? null : c.To;
                if (c.Status == CrLog.Open || c.Status == CrLog.Asked)
                {
                    string who = CrCli.WhoDecides(c);
                    if (who == CrClassifier.ArbiterRoute)
                        yield return new Gap { Kind = "CR_RULE", Feature = feature, Actor = "agent", Severity = 3, Role = TestRoles.Arbiter,
                            Detail = c.Id + " (" + c.Kind + ") " + c.From + " → " + c.To + ": " + c.Text + " → another session: ./Tools/test-gate.sh cr rule " + c.Id + " keep|reject|ask \"<why>\"" };
                    else
                        yield return new Gap { Kind = "CR_DECIDE", Feature = feature, Actor = "human", Severity = 4,
                            Detail = c.Id + " (" + c.Kind + ") " + c.From + " → " + c.To + ": " + c.Text + " → ./Tools/test-gate.sh cr decide" };
                }
                else if (c.Status == CrLog.Approved && Age(c, now) > OverdueHours)
                    yield return new Gap { Kind = "CR_OVERDUE", Feature = feature, Actor = "human", Severity = 3,
                        Detail = c.Id + " " + c.From + " → " + c.To + " approved " + Math.Round(Age(c, now)) + " h ago and still not done: " + c.From + " waits on it (start a session on it)" };
            }
        }

        public static List<CrView> Views(Dictionary<string, CrEntry> crs, DateTime now) =>
            crs.Values.Where(c => !c.Closed && c.Status != CrLog.Delivered).OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new CrView
            {
                Id = c.Id, Status = c.Status, Kind = c.Kind, From = c.From, To = c.To, Text = c.Text,
                Decides = c.Status == CrLog.Open || c.Status == CrLog.Asked ? CrCli.WhoDecides(c) : null, AgeHours = Math.Round(Age(c, now), 1)
            }).ToList();

        /// <summary>Priority rank of an item: a CR's work is as urgent as the most urgent feature waiting for it (overdue = first).</summary>
        public static int Rank(PlanReport report, WorkItem item, int own)
        {
            int rank = own;
            foreach (var id in item.Requirements.Where(r => r.StartsWith("CR-", StringComparison.Ordinal)))
            {
                var cr = report.Crs.FirstOrDefault(c => c.Id == id);
                var waiting = cr == null ? null : report.Feature(cr.From);
                if (cr != null && cr.AgeHours > OverdueHours)
                    return -1;
                if (waiting != null && waiting.Priority != null && waiting.Priority.Length == 2 && char.IsDigit(waiting.Priority[1]))
                    rank = Math.Min(rank, waiting.Priority[1] - '0');
            }
            return rank;
        }

        private static double Age(CrEntry c, DateTime now) =>
            DateTime.TryParse(c.FiledTs, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? (now - t).TotalHours : 0;
    }
}
