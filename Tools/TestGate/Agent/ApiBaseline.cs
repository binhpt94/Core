using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    /// <summary>
    /// PublicApi.txt baselines (ARCH023) accepted by the owner's yes/no instead of a hand copy:
    ///   api status            groups whose compiled API differs from their baseline (from the last ARCH023 run)
    ///   api accept &lt;Group&gt;    owner command: the new API (Dashboard/Data/api/&lt;Group&gt;.received.txt, written by ARCH023)
    ///                         becomes the baseline. The dialog shows the +/- lines; the grant is bound to that exact content,
    ///                         and the CLI regenerates the API before writing: if it changed since the owner was asked, it refuses.
    /// </summary>
    public static class ApiBaseline
    {
        public const string ReceivedDir = "Dashboard/Data/api";
        public const string AcceptedEvent = "API_BASELINE_ACCEPTED";
        private static readonly Regex ContractsGroup = new Regex(@"^Game\.([A-Za-z0-9]+)\.Contracts$");

        public sealed class Change
        {
            public string Group, Baseline, Content, Hash;
            public List<string> Added = new List<string>(), Removed = new List<string>();
            public bool Breaking => Removed.Count > 0;
        }

        /// <summary>Game.Core → Core/Runtime/PublicApi.txt; Game.X.Contracts → Features/X/Contracts/PublicApi.txt; otherwise null.</summary>
        public static string BaselinePath(string root, string group)
        {
            if (group == "Game.Core")
                return "UnityProject/Assets/_Game/Core/Runtime/PublicApi.txt";
            var m = ContractsGroup.Match(group ?? "");
            if (!m.Success)
                return null;
            string dir = "UnityProject/Assets/_Game/Features/" + m.Groups[1].Value + "/Contracts";
            return Directory.Exists(Path.Combine(root, dir)) ? dir + "/PublicApi.txt" : null;
        }

        private static string Received(string root, string group) => Path.Combine(root, ReceivedDir, group + ".received.txt");

        private static string Norm(string s) => (s ?? "").Replace("\r\n", "\n");

        /// <summary>What accepting would change for this group (from the received file); (null, error) when there is nothing valid.</summary>
        public static (Change change, string error) Pending(string root, string group)
        {
            string baseline = BaselinePath(root, group);
            if (baseline == null)
                return (null, "unknown API group '" + group + "' (Game.Core or Game.<Feature>.Contracts with a Contracts folder)"
                              + Known(root));
            string received = Received(root, group);
            if (!File.Exists(received))
                return (null, "no new API recorded for " + group + ": run dotnet test Tools/LogicTests first (ARCH023 writes it when the API differs)");
            string content = Norm(File.ReadAllText(received));
            string full = Path.Combine(root, baseline);
            string expected = File.Exists(full) ? Norm(File.ReadAllText(full)) : "";
            if (content == expected)
                return (null, group + ": the baseline already matches the last recorded API (nothing to accept)");
            var exp = new HashSet<string>(expected.Split('\n').Where(l => l.Length > 0));
            var act = new HashSet<string>(content.Split('\n').Where(l => l.Length > 0));
            var c = new Change { Group = group, Baseline = baseline, Content = content, Hash = OwnerAsk.Sha(content) };
            c.Added.AddRange(content.Split('\n').Where(l => l.Length > 0 && !exp.Contains(l)));
            c.Removed.AddRange(expected.Split('\n').Where(l => l.Length > 0 && !act.Contains(l)));
            return (c, null);
        }

        /// <summary>Groups whose last recorded API differs from the baseline (for `api status` and the dashboard).</summary>
        public static List<Change> PendingAll(string root)
        {
            string dir = Path.Combine(root, ReceivedDir);
            if (!Directory.Exists(dir))
                return new List<Change>();
            return Directory.GetFiles(dir, "*.received.txt").OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Pending(root, Path.GetFileName(f).Replace(".received.txt", "")).change).Where(c => c != null).ToList();
        }

        private static string Known(string root)
        {
            var groups = PendingAll(root).Select(c => c.Group).ToList();
            return groups.Count == 0 ? "" : " · waiting: " + string.Join(", ", groups);
        }

        /// <summary>Feature folders whose asmdefs reference this group's assembly (who a breaking change hits).</summary>
        public static List<string> Users(string root, string group)
        {
            string features = Path.Combine(root, "UnityProject", "Assets", "_Game", "Features");
            if (!Directory.Exists(features))
                return new List<string>();
            string own = ContractsGroup.Match(group).Groups[1].Value;
            return Directory.GetFiles(features, "*.asmdef", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("\"" + group + "\""))
                .Select(f => Path.GetRelativePath(features, f).Replace('\\', '/').Split('/')[0])
                .Where(f => f != own).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        /// <summary>The owner's dialog (OwnerAskText shape) for `api accept Group`; (null, error) when there is nothing to accept.</summary>
        public static (string text, string error) Describe(string root, string group)
        {
            var (c, error) = Pending(root, group);
            return c == null ? (null, error) : (OwnerAskPolicyText.ApiAccept(c, group == "Game.Core" ? new List<string> { "mọi feature" } : Users(root, group)), null);
        }

        /// <summary>Recompiles the API before writing (replaceable in tests); returns an error or null.</summary>
        public static Func<string, string> Regenerate = RunArch023;

        /// <summary>Delete the recorded APIs and run ARCH023 again: afterwards the received files are exactly today's differences.</summary>
        private static string RunArch023(string root)
        {
            string dir = Path.Combine(root, ReceivedDir);
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.received.txt"))
                    File.Delete(f);
            Console.WriteLine("Compiling the public API (dotnet test Tools/LogicTests --filter ARCH023)...");
            var psi = new ProcessStartInfo("dotnet", "test Tools/LogicTests --filter FullyQualifiedName~ARCH023_PublicApiMatchesBaseline -v q --nologo")
            { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                // red ARCH023 = there are differences (expected here); anything else (build error, no test run) is a failure
                if (p.ExitCode != 0 && !output.Contains("ARCH023_PublicApiMatchesBaseline"))
                    return "could not compile the API:\n" + output.Trim();
            }
            return null;
        }

        public static int Cli(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "status";
            if (sub == "status")
            {
                var all = PendingAll(root);
                if (all.Count == 0)
                    Console.WriteLine("No API change waiting (as of the last ARCH023 run).");
                foreach (var p in all)
                    Console.WriteLine(p.Group + " → " + p.Baseline + ": +" + p.Added.Count + " / -" + p.Removed.Count + (p.Breaking ? " (BREAKING)" : "")
                                      + "  accept: ./Tools/test-gate.sh api accept " + p.Group);
                return 0;
            }
            if (sub != "accept" || args.Length != 3)
            {
                Console.Error.WriteLine("Usage: api status | api accept <Game.Core|Game.<Feature>.Contracts>");
                return 64;
            }
            string group = args[2];
            if (BaselinePath(root, group) == null)
            {
                Console.Error.WriteLine(Pending(root, group).error);
                return 64;
            }
            var auth = OwnerAsk.Authorize(root, args, "api-cli", "changing a public API baseline is the owner's decision.");
            if (auth == null)
                return 3;
            string err = Regenerate(root);
            if (err != null)
            {
                Console.Error.WriteLine(err);
                return 1;
            }
            var (c, error) = Pending(root, group);
            if (c == null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            if (!auth.Human && auth.Bound != c.Hash)
            {
                Console.Error.WriteLine("Refused: the API of " + group + " changed after the owner was asked. Nothing written; run the command again so the owner sees the current lines.");
                return 1;
            }
            Print(c);
            if (auth.Human)
            {
                Console.Write("Type the group name to accept exactly these lines: ");
                if ((Console.ReadLine() ?? "").Trim() != group)
                {
                    Console.WriteLine("Not accepted.");
                    return 1;
                }
            }
            File.WriteAllText(Path.Combine(root, c.Baseline), c.Content);
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(AcceptedEvent, auth.Source).With("group", group).With("path", c.Baseline).With("hash", c.Hash)
                .With("added", c.Added.Count.ToString()).With("removed", c.Removed.Count.ToString()).With("toolUseId", auth.ToolUseId));
            Console.WriteLine("Accepted: " + c.Baseline + " is the new baseline of " + group + ".");
            return 0;
        }

        private static void Print(Change c)
        {
            Console.WriteLine(c.Group + " → " + c.Baseline);
            foreach (var l in c.Removed)
                Console.WriteLine("  - " + l);
            foreach (var l in c.Added)
                Console.WriteLine("  + " + l);
        }
    }
}
