using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;

namespace TestGate.AgentControl
{
    /// <summary>
    /// Owner files that used to be edited by hand, changed by one owner command each (yes/no in Claude Code, OwnerAsk):
    ///   policy vendor add id=… tier=… assemblies=… (package=… version=… | source=Assets/ThirdParty/X) [adapter=…]
    ///                                 one vendor line in Tools/ArchitecturePolicy.txt (sha256 of a source folder: computed here)
    ///   policy vendor update &lt;id&gt;      re-pin a registered vendor to what is on disk today (manifest version / folder sha256)
    ///   mcp allow read|write|exec &lt;tool|prefix*&gt;   one line in Tools/mcp-policy.txt
    /// Every line is validated before the owner is asked (an invalid one is refused, nobody is asked) and again when it runs;
    /// the grant is bound to the exact line shown.
    /// </summary>
    public static class PolicyCli
    {
        public const string VendorEvent = "POLICY_VENDOR_CHANGED";
        public const string McpEvent = "MCP_POLICY_CHANGED";
        private static readonly string[] VendorKeys = { "id", "tier", "assemblies", "package", "version", "source", "adapter" };
        private static readonly Regex McpPattern = new Regex(@"^mcp__[A-Za-z0-9_.-]+(?:__[A-Za-z0-9_.-]*)?\*?$");

        /// <summary>A planned change to an owner file: the line to write (and the one it replaces), plus the dialog.</summary>
        public sealed class Plan
        {
            public string File, Line, Replaces, Text;
            public string Bind => OwnerAsk.Sha(File + "\n" + Replaces + "\n" + Line);
        }

        // ------------------------------------------------------------------ vendor

        public static (Plan plan, string error) PlanVendor(string root, string[] args)
        {
            if (args.Length >= 1 && args[0] == "update")
                return args.Length == 2 ? PlanVendorUpdate(root, args[1]) : (null, "usage: policy vendor update <id>");
            if (args.Length < 4 || args[0] != "add")
                return (null, "usage: policy vendor add id=<id> tier=foundation|presentation|sdk assemblies=<A,B> (package=<upm> version=<v> | source=Assets/ThirdParty/<X>) [adapter=<Game.F.Unity>]");
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in args.Skip(1))
            {
                int eq = a.IndexOf('=');
                string k = eq > 0 ? a.Substring(0, eq) : a;
                if (eq <= 0 || !VendorKeys.Contains(k) || kv.ContainsKey(k))
                    return (null, "unexpected '" + a + "' (keys: " + string.Join(", ", VendorKeys) + "; sha256 is computed by the gate)");
                kv[k] = a.Substring(eq + 1);
            }
            if (kv.TryGetValue("source", out var src))
            {
                string dir = Path.Combine(root, Layout.FromUnity(src));
                if (!src.StartsWith("Assets/ThirdParty/", StringComparison.Ordinal) || !Directory.Exists(dir))
                    return (null, "source=" + src + ": not a folder under Assets/ThirdParty/");
                kv["sha256"] = ThirdPartyRules.HashFolder(dir);
            }
            if (kv.TryGetValue("package", out var pkg))
            {
                string actual = ManifestVersion(root, pkg);
                if (actual == null)
                    return (null, "package " + pkg + " is not in UnityProject/Packages/manifest.json (add it there first)");
                if (!kv.TryGetValue("version", out var ver) || ver != actual)
                    return (null, "version= must be what the manifest pins: version=" + actual);
            }
            string line = "vendor " + string.Join(" ", new[] { "id", "tier", "assemblies", "package", "version", "source", "sha256", "adapter" }
                .Where(kv.ContainsKey).Select(k => k + "=" + kv[k]));
            var current = ReadLines(root, ArchitecturePolicy.FileName);
            var before = ArchitecturePolicy.Parse(current);
            var after = ArchitecturePolicy.Parse(current.Concat(new[] { line }).ToList());
            var errors = after.ParseErrors.Where(e => e.Line == current.Count + 1).Select(e => e.Message).ToList();
            if (errors.Count > 0)
                return (null, string.Join("; ", errors));
            var v = after.Vendors.Last();
            if (before.Vendors.Any(x => x.Id == v.Id))
                return (null, "vendor '" + v.Id + "' is already registered (to re-pin it: policy vendor update " + v.Id + ")");
            var taken = v.Assemblies.Where(a => before.VendorOf(a) != null).ToList();
            if (taken.Count > 0)
                return (null, "assemblies already registered: " + string.Join(", ", taken));
            if (v.Adapter != null && !AsmdefNames(root).Contains(v.Adapter))
                return (null, "adapter=" + v.Adapter + ": no assembly with that name");
            string text = OwnerAskPolicyText.VendorAdd(line, kv["tier"].ToLowerInvariant(), Referencing(root, v.Assemblies));
            return (new Plan { File = ArchitecturePolicy.FileName, Line = line, Text = text }, null);
        }

        private static (Plan, string) PlanVendorUpdate(string root, string id)
        {
            var lines = ReadLines(root, ArchitecturePolicy.FileName);
            var v = ArchitecturePolicy.Parse(lines).Vendors.FirstOrDefault(x => x.Id == id);
            if (v == null)
                return (null, "no vendor '" + id + "' in " + ArchitecturePolicy.FileName);
            string old = lines[v.Line - 1].Trim(), line;
            if (!string.IsNullOrEmpty(v.Package))
            {
                string actual = ManifestVersion(root, v.Package);
                if (actual == null)
                    return (null, "package " + v.Package + " is not in the manifest any more (remove the vendor line instead)");
                line = Regex.Replace(old, @"\bversion=\S+", "version=" + actual);
            }
            else
            {
                string dir = Path.Combine(root, v.Source);
                if (!Directory.Exists(dir))
                    return (null, "source folder " + v.Source + " not found");
                string sha = ThirdPartyRules.HashFolder(dir);
                line = Regex.IsMatch(old, @"\bsha256=") ? Regex.Replace(old, @"\bsha256=\S+", "sha256=" + sha) : old + " sha256=" + sha;
            }
            if (line == old)
                return (null, "vendor '" + id + "' already matches what is on disk (nothing to re-pin)");
            string text = OwnerAskPolicyText.VendorAdd(line, v.Tier.ToString().ToLowerInvariant(), Referencing(root, v.Assemblies))
                .Replace("Cho phép dùng thư viện ngoài (vendor) này trong game?", "Chấp nhận phiên bản/nội dung MỚI của thư viện ngoài '" + id + "'?")
                .Replace("Dòng sẽ thêm: ", "Dòng cũ: " + old + "\n    Dòng mới: ");
            return (new Plan { File = ArchitecturePolicy.FileName, Line = line, Replaces = old, Text = text }, null);
        }

        private static string ManifestVersion(string root, string package)
        {
            string manifest = Path.Combine(root, "UnityProject", "Packages", "manifest.json");
            var m = Regex.Match(File.Exists(manifest) ? File.ReadAllText(manifest) : "", "\"" + Regex.Escape(package) + "\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static IEnumerable<string> Asmdefs(string root)
        {
            string game = Path.Combine(root, "UnityProject", "Assets", "_Game");
            return Directory.Exists(game) ? Directory.GetFiles(game, "*.asmdef", SearchOption.AllDirectories) : new string[0];
        }

        private static HashSet<string> AsmdefNames(string root) => new HashSet<string>(Asmdefs(root)
            .Select(f => Regex.Match(File.ReadAllText(f), "\"name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value), StringComparer.Ordinal);

        private static List<string> Referencing(string root, string[] assemblies) => Asmdefs(root)
            .Where(f => { var t = File.ReadAllText(f); return assemblies.Any(a => t.Contains("\"" + a + "\"") || t.Contains("\"" + a + ".dll\"")); })
            .Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // ------------------------------------------------------------------ MCP

        public static (Plan plan, string error) PlanMcp(string root, string kind, string pattern)
        {
            if (kind != "read" && kind != "write" && kind != "exec")
                return (null, "usage: mcp allow read|write|exec <mcp__server__tool | mcp__server__prefix*> (deny lines stay hand-written)");
            if (pattern == null || !McpPattern.IsMatch(pattern))
                return (null, "'" + pattern + "' is not an MCP tool name or prefix* (mcp__<server>__<tool>)");
            var overrides = McpPolicy.Load(root);
            var want = kind == "read" ? McpKind.Read : kind == "write" ? McpKind.Write : McpKind.ExecAllowed;
            string probe = pattern.TrimEnd('*');
            var now = McpPolicy.Classify(probe, overrides);
            if (now == want && overrides.Any(o => o.Item2 == pattern && o.Item1 == want))
                return (null, pattern + " is already " + kind + " in " + McpPolicy.File_);
            string nowText = now == McpKind.Read ? "đọc (tự do)" : now == McpKind.Write ? "ghi (cần task)" : now == McpKind.ExecAllowed ? "chạy code (đã được phép)"
                : now == McpKind.Exec ? "chạy code tuỳ ý (đang BỊ CHẶN)" : "BẠN ĐÃ CẤM nó bằng một dòng deny trong file; Yes sẽ gỡ lệnh cấm đó";
            string line = kind + "  " + pattern;
            return (new Plan { File = McpPolicy.File_, Line = line, Text = OwnerAskPolicyText.McpAllow(kind, pattern, nowText) }, null);
        }

        // ------------------------------------------------------------------ CLI

        public static int Cli(string root, string[] args)
        {
            bool vendor = args[0] == "policy";
            if (vendor && (args.Length < 2 || args[1] != "vendor"))
            {
                Console.Error.WriteLine("Usage: policy vendor add … | policy vendor update <id>");
                return 64;
            }
            if (!vendor && (args.Length != 4 || args[1] != "allow"))
            {
                Console.Error.WriteLine("Usage: mcp allow read|write|exec <tool|prefix*>");
                return 64;
            }
            var (plan, error) = vendor ? PlanVendor(root, args.Skip(2).ToArray()) : PlanMcp(root, args[2], args[3]);
            if (plan == null)
            {
                Console.Error.WriteLine(error);
                return 64;
            }
            var auth = OwnerAsk.Authorize(root, args, vendor ? "policy-cli" : "mcp-cli", "changing " + plan.File + " is the owner's decision.");
            if (auth == null)
                return 3;
            if (!auth.Human && auth.Bound != plan.Bind)
            {
                Console.Error.WriteLine("Refused: what this command would write changed after the owner was asked. Nothing written; run it again.");
                return 1;
            }
            Console.WriteLine(plan.File + ": " + (plan.Replaces != null ? plan.Replaces + "\n  → " : "+ ") + plan.Line);
            if (auth.Human)
            {
                Console.Write("Type yes to write exactly this line: ");
                if ((Console.ReadLine() ?? "").Trim() != "yes")
                {
                    Console.WriteLine("Not written.");
                    return 1;
                }
            }
            var lines = ReadLines(root, plan.File);
            if (plan.Replaces != null)
                lines[lines.FindIndex(l => l.Trim() == plan.Replaces)] = plan.Line;
            else
                lines.Add(plan.Line);
            File.WriteAllText(Path.Combine(root, plan.File), string.Join("\n", lines) + "\n");
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(vendor ? VendorEvent : McpEvent, auth.Source).With("file", plan.File).With("line", plan.Line)
                .With("replaces", plan.Replaces).With("toolUseId", auth.ToolUseId));
            Console.WriteLine("Written.");
            return 0;
        }

        private static List<string> ReadLines(string root, string rel)
        {
            string p = Path.Combine(root, rel);
            if (!File.Exists(p))
                return new List<string>();
            var lines = File.ReadAllText(p).Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1] == "")
                lines.RemoveAt(lines.Count - 1);
            return lines;
        }
    }
}
