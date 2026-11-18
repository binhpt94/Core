using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    /// <summary>Exec = runs arbitrary code, blocked; ExecAllowed = the owner allowed that tool ("exec" line), observed like Write.</summary>
    public enum McpKind { Read, Write, Exec, ExecAllowed, Deny }

    /// <summary>
    /// What an MCP tool (Unity, Blender, ...) may do. Tool names differ per server, so the default is a name heuristic,
    /// overridden by the owner in tools/mcp-policy.txt (agents cannot edit tools/). Unknown tools are WRITE: observed with
    /// git snapshots like Bash, judged by the task scope, never attributed to a human.
    ///   Read  : get/list/find/inspect/screenshot/run_tests... free, recorded as TOOL_USED
    ///   Write : needs an active task; files it changes are FILE_MODIFIED by the agent (scope, per-file rules, Stop)
    ///   Exec  : runs arbitrary code (execute_blender_code, execute_menu_item, run C#): its effects can't be scoped (it can
    ///           write outside the repo), so it is BLOCKED unless the owner allows that tool in tools/mcp-policy.txt
    /// </summary>
    public static class McpPolicy
    {
        public const string File_ = "tools/mcp-policy.txt";

        private static readonly Regex ExecName = new Regex(@"(execute|exec_|_exec|eval|run_?code|run_?script|run_?python|python|script|menu|shell|command|console|invoke)", RegexOptions.IgnoreCase);
        private static readonly Regex ReadName = new Regex(@"^(get|list|read|find|search|inspect|query|describe|show|view|screenshot|capture|status|info|count|validate|check|ping|fetch|run_?tests?|lookup|preview)(_|$)", RegexOptions.IgnoreCase);

        public static bool IsMcp(string tool) => tool != null && tool.StartsWith("mcp__", StringComparison.Ordinal);

        /// <summary>"mcp__unity__manage_prefabs" → "manage_prefabs".</summary>
        public static string ShortName(string tool)
        {
            int i = tool.IndexOf("__", 5, StringComparison.Ordinal);
            return i < 0 ? tool.Substring(5) : tool.Substring(i + 2);
        }

        public static McpKind Classify(string tool, IReadOnlyList<(McpKind kind, string pattern)> overrides = null)
        {
            if (overrides != null)
                foreach (var (kind, pattern) in overrides.Reverse()) // last matching line wins
                    if (Matches(tool, pattern))
                        return kind;
            string n = ShortName(tool);
            if (ReadName.IsMatch(n))   // starts with a read verb: get_script, read_console are reads
                return McpKind.Read;
            if (ExecName.IsMatch(n))
                return McpKind.Exec;
            return McpKind.Write;
        }

        private static bool Matches(string tool, string pattern) =>
            pattern.EndsWith("*", StringComparison.Ordinal) ? tool.StartsWith(pattern.TrimEnd('*'), StringComparison.Ordinal) : tool == pattern;

        /// <summary>Lines: "read|write|exec|deny &lt;tool or prefix*&gt;"; "#" comments. "exec" = the owner allows that code-running tool.</summary>
        public static List<(McpKind, string)> Load(string root)
        {
            var list = new List<(McpKind, string)>();
            string p = Path.Combine(root ?? ".", File_);
            if (!File.Exists(p))
                return list;
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw.Split('#')[0].Trim();
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    continue;
                switch (parts[0].ToLowerInvariant())
                {
                    case "read": list.Add((McpKind.Read, parts[1])); break;
                    case "write": list.Add((McpKind.Write, parts[1])); break;
                    case "exec": list.Add((McpKind.ExecAllowed, parts[1])); break;
                    case "deny": list.Add((McpKind.Deny, parts[1])); break;
                }
            }
            return list;
        }

        /// <summary>Repo paths named in the tool input (path, asset_path, prefab_path, ...), for a pre-check of the scope.</summary>
        public static IEnumerable<string> TargetPaths(HookInput h) =>
            h.ToolInput.Where(kv => kv.Key.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value != null && !kv.Value.StartsWith("{", StringComparison.Ordinal)
                                    && !kv.Value.StartsWith("[", StringComparison.Ordinal))
                       .Select(kv => kv.Value);
    }

    /// <summary>
    /// Is this process running in a coding agent's shell? Human-only commands (approve/accept) refuse there. Claude Code sets
    /// CLAUDECODE=1; Codex sets CODEX_* in its sandbox; our own settings (.claude/settings.json "env", .codex/config.toml
    /// shell_environment_policy) add GAME_AGENT_SHELL so any agent we configure is marked. Plus: no terminal = not a human.
    /// Defense in depth: the PreToolUse hook also refuses these commands before they run (both agents), and the settings deny them.
    /// </summary>
    public static class AgentShell
    {
        private static readonly string[] Markers = { "GAME_AGENT_SHELL", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CURSOR_AGENT", "GEMINI_CLI", "AIDER_MODEL" };

        public static string Detect(Func<string, string> env, IEnumerable<string> allNames)
        {
            foreach (var m in Markers)
                if (!string.IsNullOrEmpty(env(m)))
                    return m;
            return allNames.FirstOrDefault(n => n.StartsWith("CODEX_", StringComparison.Ordinal));
        }

        public static string Detect() =>
            Detect(Environment.GetEnvironmentVariable, Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString()));

        public static bool IsAgent => Detect() != null;

        /// <summary>A person at an interactive terminal (not an agent's shell, not piped input).</summary>
        public static bool IsHumanTerminal => !IsAgent && !Console.IsInputRedirected;
    }
}
