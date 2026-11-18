using System;
using System.Linq;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Which consecutive events of one lane share a node on the execution graph. Collapsing changes the picture only:
    /// every event stays in the node's <see cref="StepView.Seqs"/>, and the reducer still applies every event to the evidence.
    ///   browse      Read/Grep/Glob/...
    ///   command-ro  Bash commands that changed no file (reading through the shell)
    ///   mcp         MCP tool calls (what they change is observed separately, like Bash)
    ///   bash        a file-changing Bash command + the files the git observer saw it change (a violation still colours the node)
    ///   outside     file events outside the repository (scratchpad, memory): not project state
    ///   gate        GATE_STARTED → TEST_RESULT… → GATE_FINISHED → STOP_ALLOWED (a blocked Stop stays its own node)
    /// </summary>
    public static class StepGroups
    {
        private const int MaxDetailLines = 25;

        /// <summary>The group an event can join, or null when it is always its own node.</summary>
        public static string Of(AgentEvent e)
        {
            switch (e.Type)
            {
                case EventTypes.ToolUsed:
                    string tool = e.Get("tool") ?? "";
                    return Reducer.Browsing.Contains(tool) ? "browse" : tool.StartsWith("mcp__", StringComparison.Ordinal) ? "mcp" : null;
                case EventTypes.CommandRun:
                    return e.Get("changedFiles") == "0" ? "command-ro" : "bash";
                case EventTypes.FileModified:
                case EventTypes.UnobservedChange:
                case EventTypes.FileBaseline:
                    return IsOutsideRepo(e) ? "outside" : IsBashChange(e) ? "bash" : null;
                case EventTypes.GateStarted:
                case EventTypes.TestResult:
                case EventTypes.GateFinished:
                case EventTypes.StopAllowed:
                    return "gate";
                default:
                    return null;
            }
        }

        /// <summary>True when the event joins <paramref name="last"/> instead of starting a node. A new gate run always starts one.</summary>
        public static bool Joins(AgentEvent e, string group, StepView last, string lane)
        {
            if (group == null || last == null || last.Lane != lane)
                return false;
            if (e.Type == EventTypes.GateStarted || e.Type == EventTypes.CommandRun && group == "bash")
                return false;                                   // a new gate run / a new file-changing command starts its own node
            if (last.Kind == "bash" && IsBashChange(e))
                return true;                                    // every file that command changed, inside or outside the repo
            return last.Kind == group;
        }

        /// <summary>A file change the git observer attributes to a Bash command (it follows that command's COMMAND_RUN).</summary>
        private static bool IsBashChange(AgentEvent e) => e.Type == EventTypes.FileModified && e.Get("via") == "Bash";

        /// <summary>Repository paths are recorded relative to the root; anything absolute (or marked so by the hook) is outside.</summary>
        public static bool IsOutsideRepo(AgentEvent e)
        {
            if (e.Get("scope") == "outside the repository")
                return true;
            string path = e.Get("path");
            return path != null && (path.StartsWith("/", StringComparison.Ordinal) || path.Length > 2 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));
        }

        /// <summary>Title of a node that starts a group (single events keep their own title where it says more).</summary>
        public static string FirstTitle(string group, AgentEvent e) =>
            group == "browse" ? "Đọc / tìm × 1" : group == "outside" ? "File ngoài repo × 1" : Reducer.TitleOf(e);

        public static void Join(StepView step, AgentEvent e, string trust)
        {
            step.Seqs.Add(e.Seq);
            step.Trust = Worse(step.Trust, trust);
            int n = step.Seqs.Count;
            switch (step.Kind)
            {
                case "browse": step.Title = "Đọc / tìm × " + n; break;
                case "command-ro": step.Title = "Bash không đổi file × " + n; break;
                case "outside": step.Title = "File ngoài repo × " + n; break;
                case "bash":
                    int cut = step.Title.IndexOf(" · đổi ", StringComparison.Ordinal);
                    step.Title = step.Title.StartsWith("Bash: ", StringComparison.Ordinal)
                        ? (cut < 0 ? step.Title : step.Title.Substring(0, cut)) + " · đổi " + (n - 1) + " file"
                        : "Bash đổi " + n + " file";
                    break;
                case "mcp":
                    string tools = string.Join(", ", step.Detail.Split('\n').Select(l => l.StartsWith("tool: ", StringComparison.Ordinal) ? Short(l.Substring(6)) : null)
                        .Concat(new[] { Short(e.Get("tool")) }).Where(t => t != null).Distinct());
                    step.Title = "MCP × " + n + ": " + (tools.Length > 40 ? tools.Substring(0, 40) + "…" : tools);
                    break;
                case "gate":
                    if (e.Type == EventTypes.GateFinished)
                        step.Title = Reducer.TitleOf(e);
                    else if (e.Type == EventTypes.StopAllowed && !step.Title.EndsWith(" · cho kết thúc", StringComparison.Ordinal))
                        step.Title += " · cho kết thúc";
                    break;
            }
            if (step.Detail.Split('\n').Length < MaxDetailLines)
                step.Detail += "\n" + Reducer.Describe(e);
        }

        private static string Short(string tool)
        {
            if (string.IsNullOrEmpty(tool))
                return null;
            int i = tool.LastIndexOf("__", StringComparison.Ordinal);
            return i >= 0 ? tool.Substring(i + 2) : tool;
        }

        /// <summary>The more alarming of two trust levels: a group shows its worst event.</summary>
        public static string Worse(string a, string b) => Rank(b) > Rank(a) ? b : a;

        private static int Rank(string t)
        {
            switch (t)
            {
                case Trust.Violation: return 9;
                case Trust.Failed: return 8;
                case Trust.Unobserved: return 7;
                case Trust.Denied: return 6;
                case Trust.Pending: return 5;
                case Trust.Claim: return 4;
                case Trust.Verified: return 3;
                case Trust.Observed: return 2;
                case Trust.Planned: return 1;
                default: return 0;
            }
        }
    }
}
