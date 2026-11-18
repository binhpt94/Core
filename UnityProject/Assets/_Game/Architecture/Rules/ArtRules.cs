using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    public sealed class ArtSlotDeclaration
    {
        public string Id { get; set; }
        public string Feature { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
    }

    public sealed class ArtSlotBindingFile
    {
        public string Path { get; set; }
        public string SlotId { get; set; }
        /// <summary>GUIDs of the assets bound as candidate / final ("" when empty).</summary>
        public string CandidateGuid { get; set; }
        public string FinalGuid { get; set; }
    }

    /// <summary>
    /// ARCH120: an ArtSlotAsset (.asset, Force Text) must name a slot declared in code, of its own feature.
    /// ARCH121: slot contracts are declared in Features/F/Logic/Domain/*ArtSlots.cs with a unique id "F.Name".
    /// Static (no Unity): the art ↔ gameplay link can't silently point at nothing.
    /// </summary>
    public static class ArtRules
    {
        private static readonly Regex NewContract = new Regex(@"new\s+ArtSlotContract\s*\(\s*(?:""(?<lit>[^""]+)""|(?<ident>\w+))");
        private static readonly Regex ConstString = new Regex(@"const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""");
        private static readonly Regex SlotIdLine = new Regex(@"^\s*slotId:\s*(?<id>\S*)\s*$", RegexOptions.Multiline);
        private static readonly Regex Ref = new Regex(@"^\s*(?<field>candidate|final):\s*\{fileID:\s*-?\d+(?:,\s*guid:\s*(?<guid>[0-9a-f]{32}))?", RegexOptions.Multiline);

        public static List<ArtSlotDeclaration> Declarations(string repoRoot)
        {
            var list = new List<ArtSlotDeclaration>();
            string features = Path.Combine(repoRoot, "Assets", "_Game", "Features");
            if (!Directory.Exists(features))
                return list;
            foreach (var file in Directory.EnumerateFiles(features, "*.cs", SearchOption.AllDirectories))
            {
                string rel = PathInfo.Normalize(file.Substring(repoRoot.Length));
                var info = PathInfo.Of(rel);
                if (info.IsTest)
                    continue;
                string code = SourceText.StripComments(File.ReadAllText(file));
                if (!code.Contains("ArtSlotContract"))
                    continue;
                var consts = ConstString.Matches(code).Cast<Match>().GroupBy(m => m.Groups["name"].Value).ToDictionary(g => g.Key, g => g.First().Groups["value"].Value);
                foreach (Match m in NewContract.Matches(code))
                {
                    string id = m.Groups["lit"].Success ? m.Groups["lit"].Value : consts.TryGetValue(m.Groups["ident"].Value, out var v) ? v : null;
                    list.Add(new ArtSlotDeclaration { Id = id ?? "?" + m.Groups["ident"].Value, Feature = info.Feature, File = rel, Line = SourceText.LineOf(code, m.Index) });
                }
            }
            return list;
        }

        public static List<ArtSlotBindingFile> Bindings(string repoRoot)
        {
            var list = new List<ArtSlotBindingFile>();
            string game = Path.Combine(repoRoot, "Assets", "_Game");
            if (!Directory.Exists(game))
                return list;
            foreach (var file in Directory.EnumerateFiles(game, "*.asset", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                var m = SlotIdLine.Match(text);
                if (!m.Success)
                    continue;
                var b = new ArtSlotBindingFile { Path = PathInfo.Normalize(file.Substring(repoRoot.Length)), SlotId = m.Groups["id"].Value, CandidateGuid = "", FinalGuid = "" };
                foreach (Match r in Ref.Matches(text))
                {
                    string g = r.Groups["guid"].Success ? r.Groups["guid"].Value : "";
                    if (r.Groups["field"].Value == "candidate") b.CandidateGuid = g; else b.FinalGuid = g;
                }
                list.Add(b);
            }
            return list;
        }

        public static IEnumerable<RuleViolation> Check(string repoRoot)
        {
            var decl = Declarations(repoRoot);
            foreach (var d in decl)
            {
                if (d.Id.StartsWith("?", StringComparison.Ordinal))
                    yield return new RuleViolation("ARCH121", d.File, d.Line, "art slot id must be a string literal or a same-file const (found " + d.Id.Substring(1) + ")");
                else if (d.Feature == null || !d.Id.StartsWith(d.Feature + ".", StringComparison.Ordinal))
                    yield return new RuleViolation("ARCH121", d.File, d.Line, "art slot id '" + d.Id + "' must start with its feature: '" + d.Feature + ".<Name>'");
                if (!d.File.Contains("/Logic/Domain/") || !d.File.EndsWith("ArtSlots.cs", StringComparison.Ordinal))
                    yield return new RuleViolation("ARCH121", d.File, d.Line, "declare art slots in Features/" + d.Feature + "/Logic/Domain/<Feature>ArtSlots.cs (one catalog per feature)");
            }
            foreach (var dup in decl.GroupBy(d => d.Id).Where(g => g.Count() > 1))
                yield return new RuleViolation("ARCH121", dup.First().File, dup.First().Line, "art slot id '" + dup.Key + "' declared " + dup.Count() + " times");

            var ids = new HashSet<string>(decl.Select(d => d.Id), StringComparer.Ordinal);
            foreach (var b in Bindings(repoRoot))
            {
                if (!ids.Contains(b.SlotId))
                    yield return new RuleViolation("ARCH120", b.Path, 0, "binds art to slot '" + b.SlotId + "', which no *ArtSlots catalog declares (typo or removed slot)");
                var feature = PathInfo.Of(b.Path).Feature;
                if (feature != null && ids.Contains(b.SlotId) && !b.SlotId.StartsWith(feature + ".", StringComparison.Ordinal))
                    yield return new RuleViolation("ARCH120", b.Path, 0, "binds slot '" + b.SlotId + "' of another feature; bindings live with the feature that owns the slot");
            }
        }
    }
}
