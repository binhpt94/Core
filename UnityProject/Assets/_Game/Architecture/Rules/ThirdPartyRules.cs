using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Third-party boundary. Vendor code itself is never scanned by the strict rules (it lives outside
    /// Assets/_Game), so no blanket exceptions are needed. What IS checked is the contact surface:
    /// ARCH092 DLLs must not be auto-referenced · ARCH093 every vendor file is registered and unmodified ·
    /// ARCH094 who may reference which vendor · ARCH095 first-party exceptions are scoped and time-boxed.
    /// </summary>
    public static class ThirdPartyRules
    {
        private static readonly string[] TextExtensions = { ".cs", ".asmdef", ".asmref", ".json", ".txt", ".md", ".xml", ".shader", ".cginc", ".hlsl", ".yml", ".yaml" };

        public static bool IsEngineReference(string r) =>
            r.StartsWith("Unity.", StringComparison.Ordinal) || r.StartsWith("UnityEngine", StringComparison.Ordinal) || r.StartsWith("UnityEditor", StringComparison.Ordinal);

        public static IEnumerable<RuleViolation> Check(string root, ArchitecturePolicy policy, List<AsmdefInfo> asmdefs, DateTime today)
        {
            foreach (var e in policy.ParseErrors)
                yield return e;

            // ---- ARCH094: references from game assemblies to third-party assemblies
            foreach (var a in asmdefs)
            {
                var external = a.References.Where(r => !r.StartsWith("Game.", StringComparison.Ordinal) && !IsEngineReference(r) && !r.StartsWith("GUID:", StringComparison.Ordinal))
                    .Concat(a.PrecompiledReferences.Select(d => d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? d.Substring(0, d.Length - 4) : d)
                        .Where(d => d != "nunit.framework"));

                foreach (var r in external.Distinct())
                {
                    var v = policy.VendorOf(r);
                    if (v == null)
                    {
                        yield return new RuleViolation("ARCH094", a.Path, 0, a.Name + " references unregistered third-party '" + r + "'; a code owner must add it to " + ArchitecturePolicy.FileName);
                        continue;
                    }
                    if (v.Tier == VendorTier.Presentation && a.IsPure)
                        yield return new RuleViolation("ARCH094", a.Path, 0, a.Name + ": presentation library '" + r + "' is not allowed in pure code (use it in the Unity layer)");
                    if (v.Tier == VendorTier.Sdk && a.Name != v.Adapter && !(a.Layer == PathLayer.Tests && SameFeature(a.Name, v.Adapter)))
                        yield return new RuleViolation("ARCH094", a.Path, 0, a.Name + " uses SDK '" + r + "' directly; only " + v.Adapter + " may. Depend on its Contracts instead");
                }
            }

            // ---- ARCH092: vendor DLLs must be explicitly referenced (otherwise every assembly sees them)
            string assets = Path.Combine(root, "UnityProject", "Assets");
            if (Directory.Exists(assets))
            {
                foreach (var meta in Directory.EnumerateFiles(assets, "*.dll.meta", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(meta);
                    if (!text.Contains("PluginImporter:"))
                        continue;
                    if (!Regex.IsMatch(text, @"isExplicitlyReferenced: 1"))
                        yield return new RuleViolation("ARCH092", Rel(root, meta.Substring(0, meta.Length - 5)), 0,
                            "DLL is Auto Referenced: every assembly can call it, bypassing the boundary. Inspector: uncheck 'Auto Reference' (isExplicitlyReferenced: 1)");
                }
            }

            // ---- ARCH093: provenance (registered, pinned, unmodified)
            foreach (var v in policy.Vendors)
            {
                if (!string.IsNullOrEmpty(v.Source))
                {
                    string dir = Path.Combine(root, v.Source);
                    if (!Directory.Exists(dir))
                        yield return new RuleViolation("ARCH093", ArchitecturePolicy.FileName, v.Line, "vendor '" + v.Id + "': source folder " + v.Source + " not found");
                    else
                    {
                        string actual = HashFolderCached(root, dir);
                        if (string.IsNullOrEmpty(v.Sha256))
                            yield return new RuleViolation("ARCH093", ArchitecturePolicy.FileName, v.Line, "vendor '" + v.Id + "': no sha256; owner records: sha256=" + actual);
                        else if (!string.Equals(v.Sha256, actual, StringComparison.OrdinalIgnoreCase))
                            yield return new RuleViolation("ARCH093", v.Source, 0, "vendor '" + v.Id + "' files changed (sha256 " + actual.Substring(0, 12) + "… ≠ registered). Never edit vendor code; upgrades are re-approved by an owner");
                    }
                }
                else if (!string.IsNullOrEmpty(v.Package))
                {
                    string manifest = Path.Combine(root, "UnityProject", "Packages", "manifest.json");
                    string text = File.Exists(manifest) ? File.ReadAllText(manifest) : "";
                    var m = Regex.Match(text, "\"" + Regex.Escape(v.Package) + "\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success)
                        yield return new RuleViolation("ARCH093", "UnityProject/Packages/manifest.json", 0, "registered package '" + v.Package + "' is not in the manifest");
                    else if (!string.IsNullOrEmpty(v.Version) && !m.Groups[1].Value.Contains(v.Version))
                        yield return new RuleViolation("ARCH093", "UnityProject/Packages/manifest.json", 0, "package '" + v.Package + "' is '" + m.Groups[1].Value + "', policy pins " + v.Version);
                }
            }

            if (Directory.Exists(assets))
            {
                var sources = policy.Vendors.Where(v => !string.IsNullOrEmpty(v.Source)).Select(v => PathInfo.Normalize(v.Source).TrimEnd('/') + "/").ToList();
                foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
                {
                    string rel = Rel(root, file);
                    if (rel.StartsWith(PathInfo.GameRoot, StringComparison.Ordinal) || rel.EndsWith(".meta", StringComparison.Ordinal))
                        continue;
                    bool code = rel.EndsWith(".cs", StringComparison.Ordinal) || rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                || rel.EndsWith(".asmdef", StringComparison.Ordinal) || rel.EndsWith(".asmref", StringComparison.Ordinal);
                    if (code && !sources.Any(s => rel.StartsWith(s, StringComparison.Ordinal)))
                        yield return new RuleViolation("ARCH093", rel, 0, "code outside Assets/_Game that no registered vendor owns. Game code goes in Assets/_Game; vendor code must be registered");
                }
            }

            // ---- ARCH095: exceptions are first-party, scoped, explained and time-boxed
            var names = new HashSet<string>(asmdefs.Select(a => a.Name));
            var known = new HashSet<string>(RuleCatalog.All.Select(r => r.Id));
            foreach (var e in policy.Exceptions)
            {
                string where = "exception " + e.Rule + "@" + e.Assembly;
                if (string.IsNullOrEmpty(e.Rule) || !known.Contains(e.Rule))
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": unknown rule");
                else if (e.Rule.StartsWith("ARCH09", StringComparison.Ordinal))
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": policy rules cannot be excepted");
                if (string.IsNullOrEmpty(e.Assembly) || !names.Contains(e.Assembly))
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": assembly must be an existing Game.* assembly (no paths/globs)");
                if (string.IsNullOrWhiteSpace(e.Reason) || string.IsNullOrWhiteSpace(e.Owner))
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": needs owner= and reason=\"...\"");
                if (e.Expires == null)
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": needs expires=yyyy-mm-dd");
                else if (e.Expires.Value.Date < today.Date)
                    yield return new RuleViolation("ARCH095", ArchitecturePolicy.FileName, e.Line, where + ": EXPIRED on " + e.Expires.Value.ToString("yyyy-MM-dd") + "; fix the code or get it re-approved");
            }
        }

        /// <summary>True if the exception list waives this violation (valid, unexpired, same rule + owning assembly).</summary>
        public static bool IsWaived(RuleViolation v, ArchitecturePolicy policy, Func<string, string> assemblyOf, DateTime today) =>
            !v.RuleId.StartsWith("ARCH09", StringComparison.Ordinal) &&
            policy.Exceptions.Any(e => e.Rule == v.RuleId && e.Expires != null && e.Expires.Value.Date >= today.Date
                                       && !string.IsNullOrWhiteSpace(e.Reason) && !string.IsNullOrWhiteSpace(e.Owner)
                                       && e.Assembly == assemblyOf(v.Path));

        public const string CacheFile = "Dashboard/Data/vendor-hash-cache.txt";

        /// <summary>
        /// Hash with a local cache keyed by every file's (path, size, mtime): an unchanged multi-GB SDK costs a few
        /// file stats instead of a full read. Any size/mtime change → full rehash. The cache is local only
        /// (Dashboard/Data/, git-ignored); CI (env CI=true) and GAME_ARCH_NO_CACHE=1 always hash fully, so a forged
        /// mtime on a developer machine cannot get past the protected CI check.
        /// </summary>
        public static string HashFolderCached(string root, string dir)
        {
            bool noCache = Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("GAME_ARCH_NO_CACHE") == "1";
            if (noCache)
                return HashFolder(dir);

            string key = PathInfo.Normalize(dir.Substring(root.Length));
            string stamp = Stamp(dir);
            string cachePath = Path.Combine(root, CacheFile);
            var lines = File.Exists(cachePath) ? File.ReadAllLines(cachePath).ToList() : new List<string>();
            foreach (var line in lines)
            {
                var p = line.Split('|');
                if (p.Length == 3 && p[0] == key && p[1] == stamp)
                    return p[2];
            }

            string hash = HashFolder(dir);
            lines.RemoveAll(l => l.StartsWith(key + "|", StringComparison.Ordinal));
            lines.Add(key + "|" + stamp + "|" + hash);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllLines(cachePath, lines);
            }
            catch (IOException)
            {
                // cache is an optimisation only
            }
            return hash;
        }

        private static string Stamp(string dir)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var fi = new FileInfo(f);
                    sb.Append(PathInfo.Normalize(f.Substring(dir.Length))).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>Deterministic content hash of a vendor folder (.meta excluded, line endings normalised).</summary>
        public static string HashFolder(string dir)
        {
            using (var sha = SHA256.Create())
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".meta", StringComparison.Ordinal))
                    .Select(f => new { Full = f, Rel = PathInfo.Normalize(f.Substring(dir.Length)) })
                    .OrderBy(f => f.Rel, StringComparer.Ordinal);
                var buffer = new List<byte>();
                foreach (var f in files)
                {
                    buffer.AddRange(Encoding.UTF8.GetBytes(f.Rel + "\n"));
                    var bytes = File.ReadAllBytes(f.Full);
                    if (TextExtensions.Any(e => f.Rel.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        bytes = bytes.Where(b => b != (byte)'\r').ToArray();
                    buffer.AddRange(bytes);
                }
                return string.Concat(sha.ComputeHash(buffer.ToArray()).Select(b => b.ToString("x2")));
            }
        }

        private static bool SameFeature(string testAssembly, string adapter)
        {
            var t = testAssembly.Split('.');
            var a = adapter.Split('.');
            return t.Length > 1 && a.Length > 1 && t[1] == a[1];
        }

        private static string Rel(string root, string full) => PathInfo.Normalize(full.Substring(root.Length));
    }
}
