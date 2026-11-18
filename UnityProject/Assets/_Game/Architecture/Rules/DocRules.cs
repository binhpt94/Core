using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Keeps documentation honest without anyone (human or AI) "remembering to update the diagram":
    /// ARCH090 generated diagrams are regenerated and compared (stale = red, with the exact fix command);
    /// ARCH091 hand-written docs may only mention code names and paths that still exist ("anchors").
    /// </summary>
    public sealed class DocRules
    {
        private static readonly Regex InlineCode = new Regex(@"(?<!`)`([^`\n]+)`(?!`)");
        private static readonly Regex Identifier = new Regex(@"^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)*(?:\(\))?$");
        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_]*");
        private static readonly string[] PathRoots = { "UnityProject/", "Tools/", "Docs/", "Dashboard/", "Template/", ".claude/", ".github/" };
        /// <summary>Unity-relative paths in docs ("Assets/_Game/...") are checked under UnityProject/.</summary>
        private static readonly string[] UnityRoots = { "Assets/_Game/", "Assets/ThirdParty/", "ProjectSettings/", "Packages/" };
        /// <summary>Not checked: Unity menu paths (Tools/Game/...), files that appear on demand (plan ledgers, inbox, gate output).</summary>
        private static readonly string[] NotAFile = { "Tools/Game/", Layout.Plan, Layout.Data };
        private static readonly Regex NamespaceDecl = new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
        private static readonly Regex TypeDecl = new Regex(@"\b(?:class|struct|interface|enum)\s+(\w+)");
        private static readonly string[] Placeholders = { "X", "Y", "F", "A", "B", "Feature", "Name", "Vendor" };

        private readonly string _root;
        private HashSet<string> _words;
        private HashSet<string> _namespaces;
        private HashSet<string> _types;

        public DocRules(string root) => _root = root;

        /// <summary>Docs that are hand-written and therefore anchor-checked.</summary>
        public static bool IsCheckedDoc(string rel) =>
            rel.EndsWith(".md", StringComparison.Ordinal) && !rel.StartsWith(ArchitectureDocs.Dir + "/", StringComparison.Ordinal) &&
            (rel == "CLAUDE.md" || rel == "AGENTS.md" || rel == "README.md" || rel.StartsWith(Layout.Docs, StringComparison.Ordinal) && !rel.StartsWith(Layout.Plan, StringComparison.Ordinal)
             || (rel.StartsWith(PathInfo.GameRoot, StringComparison.Ordinal) && rel.EndsWith("/README.md", StringComparison.Ordinal)));

        public IEnumerable<string> CheckedDocs()
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories))
            {
                string rel = PathInfo.Normalize(f.Substring(_root.Length));
                if (!rel.StartsWith(Layout.Library, StringComparison.Ordinal) && !rel.Contains("/bin/") && !rel.Contains("/obj/") && IsCheckedDoc(rel))
                    yield return rel;
            }
        }

        /// <summary>
        /// ARCH090: every generated file must equal what the generator produces right now, no generated page may be
        /// orphaned, and the generator must have understood everything it read (fail-closed parse errors).
        /// </summary>
        public IEnumerable<RuleViolation> CheckGenerated()
        {
            var graph = ArchitectureGraph.Build(_root);
            foreach (var e in graph.ParseErrors)
                yield return e;

            var expected = ArchitectureDocs.Render(graph);
            const string fix = "run ./Tools/test-gate.sh graph --write (or Unity: Tools > Game > Regenerate System Map) and commit";
            foreach (var kv in expected)
            {
                string full = Path.Combine(_root, kv.Key);
                string actual = File.Exists(full) ? ArchitectureDocs.Normalize(File.ReadAllText(full)) : null;
                if (actual == kv.Value)
                    continue;
                if (actual == null)
                {
                    yield return new RuleViolation("ARCH090", kv.Key, 0, "missing; " + fix);
                    continue;
                }
                var exp = kv.Value.Split('\n');
                var act = new HashSet<string>(actual.Split('\n'));
                var added = exp.Where(l => !act.Contains(l) && l.Trim().Length > 0).Take(6).Select(l => "+" + l.Trim());
                var removed = actual.Split('\n').Where(l => Array.IndexOf(exp, l) < 0 && l.Trim().Length > 0).Take(6).Select(l => "-" + l.Trim());
                yield return new RuleViolation("ARCH090", kv.Key, 0, "stale vs source; " + fix + ". Changes: " + string.Join(" ", removed.Concat(added)));
            }
            foreach (var orphan in ArchitectureDocs.OrphanPages(_root, expected))
                yield return new RuleViolation("ARCH090", orphan, 0, "page of a feature that no longer exists; " + fix);
        }

        /// <summary>ARCH091 for one hand-written doc.</summary>
        public IEnumerable<RuleViolation> CheckAnchors(string rel)
        {
            string full = Path.Combine(_root, rel);
            if (!File.Exists(full))
                yield break;

            var lines = File.ReadAllLines(full);
            bool inFence = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                    continue;

                foreach (Match m in InlineCode.Matches(lines[i]))
                {
                    string token = m.Groups[1].Value.Trim();
                    string problem = CheckToken(token);
                    if (problem != null)
                        yield return new RuleViolation("ARCH091", rel, i + 1, "`" + token + "` " + problem + " (renamed/deleted? update the doc)");
                }
            }
        }

        private string CheckToken(string token)
        {
            bool unity = UnityRoots.Any(r => token.StartsWith(r, StringComparison.Ordinal));
            if (NotAFile.Any(r => token.StartsWith(r, StringComparison.Ordinal)))
                return null;
            if (unity || PathRoots.Any(r => token.StartsWith(r, StringComparison.Ordinal)))
            {
                string path = (unity ? Layout.Unity : "") + Regex.Replace(token, @":\d+$", "").TrimEnd('/');
                var segments = path.Split('/');
                if (path.IndexOfAny(new[] { '<', '>', '*', '{', ' ', '|' }) >= 0 || segments.Any(s => Placeholders.Contains(s) || s.StartsWith("<", StringComparison.Ordinal)))
                    return null;
                return File.Exists(Path.Combine(_root, path)) || Directory.Exists(Path.Combine(_root, path)) ? null : "path does not exist";
            }

            if (!Identifier.IsMatch(token))
                return null;

            // Only names that are provably OURS are checked (external APIs like `Activator.CreateInstance` are not):
            //   Game.* names → longest declared namespace/assembly prefix, the rest must be declared/used words;
            //   Type.Member  → when Type is declared in our code, Member must exist.
            // Renamed files are caught by the path check (one type per file, named after it).
            string bare = token.Replace("()", "");
            var parts = bare.Split('.');
            Load();
            if (parts[0] == "Game")
            {
                int known = 0;
                for (int n = parts.Length; n > 0; n--)
                    if (_namespaces.Contains(string.Join(".", parts.Take(n)))) { known = n; break; }
                if (known == 0)
                    return "names namespace/assembly '" + bare + "' which does not exist";
                var missing = parts.Skip(known).Where(p => !_words.Contains(p)).ToList();
                return missing.Count == 0 ? null : "names '" + string.Join(".", missing) + "' which no source file declares or uses";
            }
            if (parts.Length > 1 && _types.Contains(parts[0]))
            {
                var missing = parts.Skip(1).Where(p => !_words.Contains(p)).ToList();
                return missing.Count == 0 ? null : "names member '" + string.Join(".", missing) + "' which " + parts[0] + " no longer has";
            }
            return null;
        }

        /// <summary>Identifiers, declared namespaces (+ asmdef names) and declared types from comment-stripped C#.</summary>
        private void Load()
        {
            if (_words != null)
                return;
            _words = new HashSet<string>(StringComparer.Ordinal);
            _namespaces = new HashSet<string>(AsmdefRules.Load(_root).Select(a => a.Name), StringComparer.Ordinal);
            _types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dir in new[] { "UnityProject/Assets", "Tools" }.Select(d => Path.Combine(_root, d)).Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                        continue;
                    string code = SourceText.StripComments(File.ReadAllText(f));
                    code = Regex.Replace(code, @"@""(?:[^""]|"""")*""|""(?:\\.|[^""\\])*""", " "); // drop string literals (regex text, messages)
                    foreach (Match w in Word.Matches(code))
                        _words.Add(w.Value);
                    foreach (Match n in NamespaceDecl.Matches(code))
                    {
                        var segs = n.Groups[1].Value.Split('.');
                        for (int i = 1; i <= segs.Length; i++)
                            _namespaces.Add(string.Join(".", segs.Take(i)));
                    }
                    if (PathInfo.Of(PathInfo.Normalize(f.Substring(_root.Length))).IsGameCode)
                        foreach (Match t in TypeDecl.Matches(code))
                            _types.Add(t.Groups[1].Value);
                }
        }
    }
}
