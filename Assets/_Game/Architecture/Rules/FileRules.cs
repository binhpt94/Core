using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Where files may live, which names are junk, and whether every asset reference still resolves. No registry: the path
    /// says what a file belongs to, git says what is official, .meta says its identity, the YAML graph says who uses it.
    ///   ARCH130 file outside the known layout (stops "lung tung" files the moment they appear)
    ///   ARCH131 junk: scenes outside Scenes/, "New Scene"/"Untitled", *.tmp/.bak/.orig, "copy"/"_old" files
    ///   ARCH132 a reference to a GUID that no longer exists (moved/deleted without its .meta), with a diagnosis
    /// </summary>
    public static class FileRules
    {
        public const string Scratch = ".scratch/";

        private static readonly HashSet<string> RootEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            "Assets", "Packages", "ProjectSettings", "LogicTests", "tools", "docs", "plan", ".claude", ".github",
            "README.md", "CLAUDE.md", "AGENTS.md", "ArchitecturePolicy.txt", ".gitignore", ".gitattributes", ".editorconfig"
        };
        private static readonly string[] AssetsTop = { "Assets/_Game/", "Assets/ThirdParty/", "Assets/Plugins/", "Assets/StreamingAssets/" };
        private static readonly HashSet<string> GameTop = new HashSet<string> { "Architecture", "Bootstrap", "Core", "Features", "Scenes", "Tests" };
        private static readonly HashSet<string> CoreTop = new HashSet<string> { "Runtime", "DevTools", "Unity", "Editor", "Tests" };
        private static readonly HashSet<string> FeatureTop = new HashSet<string> { "Contracts", "Logic", "Unity", "Editor", "DevTools", "Tests", "Assets" };
        private static readonly Regex JunkName = new Regex(
            @"^(New (Scene|Prefab|Material|Animation|Animator Controller|Folder|Sprite|Text|Script|Behaviour Script)|Untitled|GameObject)( ?\(?\d+\)?)?\.|" +
            @"\.(tmp|temp|bak|orig|rej|swp)$|~$|[ _-](copy|old|backup|bak|tmp)( ?\d+)?\.[^.]+$|\(\d+\)\.[^.]+$", RegexOptions.IgnoreCase);

        /// <summary>ARCH130/131 for one repo-relative path (no IO).</summary>
        public static IEnumerable<RuleViolation> CheckPath(string relativePath)
        {
            string p = (relativePath ?? "").Replace('\\', '/'); // not PathInfo.Normalize: it strips the dot of .claude/.github
            while (p.StartsWith("./", StringComparison.Ordinal))
                p = p.Substring(2);
            if (p.Length == 0 || p.StartsWith(Scratch, StringComparison.Ordinal))
                yield break;
            string name = p.Substring(p.LastIndexOf('/') + 1);
            string target = p.EndsWith(".meta", StringComparison.Ordinal) ? p.Substring(0, p.Length - 5) : p;

            string where = Misplaced(target);
            if (where != null)
                yield return new RuleViolation("ARCH130", p, 0, where);

            if (JunkName.IsMatch(name.EndsWith(".meta", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name))
                yield return new RuleViolation("ARCH131", p, 0, "looks like a leftover (temp/copy/untitled): delete it, or give it a real name; scratch work goes to " + Scratch);
            else if (target.EndsWith(".unity", StringComparison.Ordinal) && !target.StartsWith(PathInfo.GameRoot + "Scenes/", StringComparison.Ordinal)
                     && !target.StartsWith("Assets/ThirdParty/", StringComparison.Ordinal))
                yield return new RuleViolation("ARCH131", p, 0, "scene outside Assets/_Game/Scenes: test without scenes (EditMode stages) or put throwaway scenes in " + Scratch);
        }

        /// <summary>null when the path is inside the known layout; otherwise where it should go.</summary>
        private static string Misplaced(string p)
        {
            int slash = p.IndexOf('/');
            string top = slash < 0 ? p : p.Substring(0, slash);
            if (top != "Assets")
                return RootEntries.Contains(top) || top.StartsWith(".", StringComparison.Ordinal) && top != ".scratch" ? null
                    : "unknown top-level entry '" + top + "': temp files go to " + Scratch + ", docs to docs/, tools to tools/";
            if (slash < 0 || p == "Assets/_Game" || AssetsTop.Any(a => p == a.TrimEnd('/')))
                return null;
            if (!AssetsTop.Any(a => p.StartsWith(a, StringComparison.Ordinal)))
                return "Assets/ may only contain _Game/, ThirdParty/, Plugins/, StreamingAssets/ (game code and content live in Assets/_Game/Features/<F>/)";
            if (!p.StartsWith(PathInfo.GameRoot, StringComparison.Ordinal))
                return null; // vendors: ARCH093 governs them
            var parts = p.Substring(PathInfo.GameRoot.Length).Split('/');
            if (parts.Length == 1)
                return "no loose files in Assets/_Game/: put it in a feature (Features/<F>/...)";
            if (!GameTop.Contains(parts[0]))
                return "Assets/_Game/ contains only " + string.Join(", ", GameTop.OrderBy(x => x)) + " (content belongs to a feature: Features/<F>/Assets/Private/)";
            if (parts[0] == "Core" && parts.Length == 2)
                return "no loose files in Core/: use Core/Runtime, DevTools, Unity or Editor";
            if (parts[0] == "Core" && parts.Length > 2 && !CoreTop.Contains(parts[1]))
                return "Core/ contains only " + string.Join(", ", CoreTop.OrderBy(x => x));
            if (parts[0] == "Features" && parts.Length > 3 && !FeatureTop.Contains(parts[2]))
                return "a feature contains only " + string.Join(", ", FeatureTop.OrderBy(x => x)) + " (+ README.md)";
            if (parts[0] == "Features" && parts.Length == 3 && parts[2] != "README.md" && !FeatureTop.Contains(parts[2]))
                return "loose file in the feature root: only README.md lives there";
            if (parts[0] == "Features" && parts.Length >= 4 && parts[2] == "Assets" && parts[3] != "Public" && parts[3] != "Private")
                return "feature assets go to Assets/Public/ or Assets/Private/";
            return null;
        }

        // ------------------------------------------------------------------ project-wide (needs git)

        /// <summary>Runs git; returns (exit code, stdout). Injectable for tests and for hosts without git.</summary>
        public static (int code, string output) Git(string root, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return (p.ExitCode, o);
                }
            }
            catch (Exception)
            {
                return (-1, "");
            }
        }

        /// <summary>ARCH130/131 for files outside Assets/_Game (the per-file scan covers inside) + ARCH132.</summary>
        public static IEnumerable<RuleViolation> Check(string repoRoot)
        {
            var (code, listed) = Git(repoRoot, "ls-files -co --exclude-standard");
            if (code == 0)
                foreach (var f in listed.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith(PathInfo.GameRoot, StringComparison.Ordinal)))
                    foreach (var v in CheckPath(f))
                        yield return v;
            foreach (var v in DanglingGuids(repoRoot, new AssetRules(repoRoot).GuidIndex))
                yield return v;
        }

        private static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-f]{32})");
        private static readonly Regex MetaGuid = new Regex(@"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);

        /// <summary>The base the branch started from: merge-base with origin/main, else HEAD.</summary>
        public static string BaseRef(string root)
        {
            if (Git(root, "rev-parse --verify --quiet origin/main").code == 0)
            {
                var (c, mb) = Git(root, "merge-base origin/main HEAD");
                if (c == 0 && mb.Trim().Length > 0)
                    return mb.Trim();
            }
            return Git(root, "rev-parse --verify --quiet HEAD").code == 0 ? "HEAD" : null;
        }

        /// <summary>
        /// ARCH132: YAML references to GUIDs whose .meta was deleted since the base (committed or not) and that no current .meta
        /// carries. Only guids that EXISTED in this project are judged, so package/built-in guids never false-positive.
        /// Diagnosis: a current file with the same git blob as the deleted asset = "moved without its .meta" + the exact fix.
        /// </summary>
        public static IEnumerable<RuleViolation> DanglingGuids(string root, IReadOnlyDictionary<string, string> currentIndex)
        {
            string baseRef = BaseRef(root);
            if (baseRef == null)
                yield break;
            var lost = new Dictionary<string, string>(StringComparer.Ordinal); // guid → old asset path
            var deleted = Git(root, "diff --name-only --diff-filter=D " + baseRef).output.Split('\n').Select(x => x.Trim())
                .Where(x => x.EndsWith(".meta", StringComparison.Ordinal) && x.StartsWith("Assets/", StringComparison.Ordinal));
            foreach (var meta in deleted)
            {
                var m = MetaGuid.Match(Git(root, "show \"" + baseRef + ":" + meta + "\"").output);
                if (m.Success && !currentIndex.ContainsKey(m.Groups[1].Value))
                    lost[m.Groups[1].Value] = meta.Substring(0, meta.Length - 5);
            }
            if (lost.Count == 0)
                yield break;

            string game = Path.Combine(root, "Assets");
            foreach (var file in Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories).Where(f => AssetRules.IsYaml(f)))
            {
                string text = File.ReadAllText(file);
                foreach (Match r in GuidRef.Matches(text))
                {
                    if (!lost.TryGetValue(r.Groups[1].Value, out var old))
                        continue;
                    string rel = PathInfo.Normalize(file.Substring(root.Length));
                    yield return new RuleViolation("ARCH132", rel, SourceText.LineOf(text, r.Index),
                        "references guid " + r.Groups[1].Value + " (" + old + ") which no longer exists. " + Diagnose(root, baseRef, old));
                }
            }
        }

        /// <summary>Was the asset moved (same content somewhere else) or deleted? Prints the command that fixes it.</summary>
        public static string Diagnose(string root, string baseRef, string oldPath)
        {
            string blob = Git(root, "rev-parse \"" + baseRef + ":" + oldPath + "\"").output.Trim();
            string ext = Path.GetExtension(oldPath);
            if (blob.Length == 40)
            {
                foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*" + ext, SearchOption.AllDirectories))
                {
                    string rel = PathInfo.Normalize(f.Substring(root.Length));
                    if (rel == oldPath)
                        continue;
                    if (Git(root, "hash-object \"" + rel + "\"").output.Trim() == blob)
                        return "It was MOVED to " + rel + " without its .meta (Unity gave it a new guid). Fix (keeps every reference): " +
                               "git show " + baseRef + ":" + oldPath + ".meta > \"" + rel + ".meta\"";
                }
            }
            return "It was deleted. Restore it (git checkout " + baseRef + " -- \"" + oldPath + "\" \"" + oldPath + ".meta\") or remove the reference in Unity.";
        }
    }
}
