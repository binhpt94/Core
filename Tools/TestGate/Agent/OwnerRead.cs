using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Reading the gate's raw output (Dashboard/Data/: EditMode.xml, *.trx, logs, data.json) needs the owner's yes in Claude Code,
    /// and one yes = one read:
    ///   PreToolUse   Read (file_path) / Grep (path) under Dashboard/Data/ → "ask" with the gate's dialog (what file, how big, when
    ///                written). No allow rule exists anywhere, so the next read asks again: nothing outlives the one call.
    ///                A path that doesn't exist is blocked without asking (nothing to decide).
    ///   PostToolUse  the tool ran = the owner said yes: OWNER_READ_DONE (the dashboard shows what the AI read).
    ///   Bash         cat/grep/… of Dashboard/Data is blocked (AgentHooks.ForbiddenCommand): reading goes through this ask.
    /// .agent/ stays denied (settings.json): the event log is never read by an agent.
    /// </summary>
    public static class OwnerRead
    {
        public const string Dir = "Dashboard/Data";
        public const string AskedEvent = "OWNER_READ_ASKED";
        public const string DoneEvent = "OWNER_READ_DONE";
        /// <summary>
        /// Dashboard/Data as an unquoted path argument of a shell command (cat Dashboard/Data/x, ls ./Dashboard/Data). A quoted
        /// mention (grep -rn "Dashboard/Data" Tools) is a search pattern, not a read, so it is allowed.
        /// </summary>
        public static readonly Regex PathInShell = new Regex(@"(?:^|[\s;&|(<>=])(?:\./|[^\s'""]*/)?Dashboard/Data(?:/|\s|$)");
        private static readonly HashSet<string> Tools = new HashSet<string>(StringComparer.Ordinal) { "Read", "Grep" };

        /// <summary>The Dashboard/Data path this Read/Grep targets (repo-relative), or null when it's any other call.</summary>
        public static string Target(string root, HookInput h)
        {
            if (!Tools.Contains(h.ToolName ?? ""))
                return null;
            string rel = AgentHooks.Rel(root, h.ToolName == "Read" ? h.Input("file_path") : h.Input("path"));
            return IsUnder(rel) ? rel.TrimEnd('/') : null;
        }

        public static bool IsUnder(string rel) =>
            rel != null && (rel.TrimEnd('/') == Dir || rel.StartsWith(Dir + "/", StringComparison.Ordinal));

        /// <summary>PreToolUse: -1 = not ours (other rules apply); 0 = asked (JSON on stdout); 2 = blocked.</summary>
        public static int Pre(string root, HookInput h)
        {
            string rel = Target(root, h);
            if (rel == null)
                return -1;
            string full = Path.Combine(root, rel);
            bool file = File.Exists(full);
            if (!file && !Directory.Exists(full))
            {
                string why = rel + " does not exist (the gate writes it: ./Tools/test-gate.sh run, or ./Tools/run-unity-tests.sh EditMode); the owner is not asked";
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "hook").With("tool", h.ToolName).With("target", rel).With("reason", why));
                Console.Error.WriteLine("BLOCKED: " + why);
                return 2;
            }
            string text = OwnerAskText.ReadOnce(rel, Describe(full, file), h.ToolName == "Grep" ? h.Input("pattern") : null);
            Recorder.Emit(Recorder.New(AskedEvent, "hook").With("tool", h.ToolName).With("toolUseId", h.ToolUseId).With("path", rel)
                .With("hash", file ? Hash(full) : null));
            Console.WriteLine(AskJson(text));
            return 0;
        }

        /// <summary>PostToolUse: the read ran, so the owner said yes to it.</summary>
        public static void Post(string root, HookInput h)
        {
            string rel = Target(root, h);
            if (rel != null)
                Recorder.Emit(Recorder.New(DoneEvent, "hook").With("tool", h.ToolName).With("toolUseId", h.ToolUseId).With("path", rel));
        }

        /// <summary>"kết quả test Unity EditMode · 412 KB · ghi lúc 2026-11-20 07:34 UTC" (or the folder's file count).</summary>
        private static string Describe(string full, bool file)
        {
            if (!file)
                return "cả thư mục (" + Directory.GetFiles(full, "*", SearchOption.AllDirectories).Length + " file)";
            var fi = new FileInfo(full);
            string name = fi.Name;
            string kind = name == "EditMode.xml" || name == "PlayMode.xml" ? "kết quả test Unity " + Path.GetFileNameWithoutExtension(name)
                : name.EndsWith(".trx", StringComparison.Ordinal) ? "kết quả test dotnet (LogicTests)"
                : name.EndsWith(".log", StringComparison.Ordinal) ? "log của Unity"
                : name.StartsWith("data.", StringComparison.Ordinal) ? "dữ liệu của dashboard"
                : "file do gate ghi ra";
            long kb = Math.Max(1, (fi.Length + 1023) / 1024);
            return kind + " · " + kb.ToString(CultureInfo.InvariantCulture) + " KB · ghi lúc "
                   + fi.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        private static string Hash(string full)
        {
            using (var s = File.OpenRead(full))
                return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }

        private static string AskJson(string reason) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hookSpecificOutput"] = new Dictionary<string, string>
            {
                ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "ask", ["permissionDecisionReason"] = reason
            }
        });
    }
}
