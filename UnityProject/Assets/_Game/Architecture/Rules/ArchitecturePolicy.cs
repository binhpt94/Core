using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    public enum VendorTier
    {
        /// <summary>Allowed everywhere, including pure code (e.g. UniTask).</summary>
        Foundation,
        /// <summary>Allowed in Unity-side layers only, never in Logic/Contracts (e.g. DOTween, TextMeshPro).</summary>
        Presentation,
        /// <summary>Allowed ONLY in its declared adapter assembly (ads, IAP, analytics, Firebase...).</summary>
        Sdk
    }

    public sealed class VendorPolicy
    {
        public string Id;
        public VendorTier Tier;
        public string[] Assemblies = new string[0];
        public string Adapter;   // required for Sdk
        public string Source;    // Root-relative folder (the file says Assets/ThirdParty/X) of vendored files (hash-locked)
        public string Package;   // UPM package name (version-locked via Packages/manifest.json)
        public string Version;
        public string Sha256;
        public int Line;
    }

    public sealed class ExceptionPolicy
    {
        public string Rule;
        public string Assembly;
        public DateTime? Expires;
        public string Owner;
        public string Reason;
        public int Line;
    }

    /// <summary>
    /// Tools/ArchitecturePolicy.txt (repo root, CODEOWNERS-protected, denied to agents): the ONLY hand-maintained
    /// allowlist, because it holds what cannot be derived from source: which third-party code is approved,
    /// where it may be used, its pinned version/hash, and time-boxed first-party exceptions.
    /// Line format: `kind key=value key="quoted value"`; `#` starts a comment.
    /// </summary>
    public sealed class ArchitecturePolicy
    {
        public const string FileName = "Tools/ArchitecturePolicy.txt";

        private static readonly Regex Pair = new Regex(@"(\w+)=(""([^""]*)""|\S+)");

        public List<VendorPolicy> Vendors { get; } = new List<VendorPolicy>();
        public List<ExceptionPolicy> Exceptions { get; } = new List<ExceptionPolicy>();
        public List<RuleViolation> ParseErrors { get; } = new List<RuleViolation>();
        public bool Exists { get; private set; }

        public static ArchitecturePolicy Load(string repoRoot)
        {
            string path = Path.Combine(repoRoot, FileName);
            if (!File.Exists(path))
                return new ArchitecturePolicy();
            return Parse(File.ReadAllLines(path));
        }

        /// <summary>The policy in these lines (the file's text); also how a proposed line is checked before it is written.</summary>
        public static ArchitecturePolicy Parse(IReadOnlyList<string> lines)
        {
            var policy = new ArchitecturePolicy { Exists = true };
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                string kind = line.Split(' ')[0];
                var kv = Pair.Matches(line).Cast<Match>()
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);
                int lineNo = i + 1;

                if (kind == "vendor")
                    policy.ParseVendor(kv, lineNo);
                else if (kind == "exception")
                    policy.ParseException(kv, lineNo);
                else
                    policy.Error(lineNo, "unknown entry '" + kind + "' (expected vendor/exception)");
            }
            return policy;
        }

        private void ParseVendor(Dictionary<string, string> kv, int line)
        {
            var v = new VendorPolicy { Line = line };
            kv.TryGetValue("id", out v.Id);
            kv.TryGetValue("adapter", out v.Adapter);
            kv.TryGetValue("source", out v.Source);
            if (!string.IsNullOrEmpty(v.Source) && !v.Source.StartsWith(Layout.Unity, StringComparison.Ordinal))
                v.Source = Layout.FromUnity(v.Source); // written Unity-relative (Assets/ThirdParty/X), used Root-relative
            kv.TryGetValue("package", out v.Package);
            kv.TryGetValue("version", out v.Version);
            kv.TryGetValue("sha256", out v.Sha256);
            if (kv.TryGetValue("assemblies", out var asm))
                v.Assemblies = asm.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (string.IsNullOrEmpty(v.Id)) Error(line, "vendor without id");
            if (v.Assemblies.Length == 0) Error(line, "vendor '" + v.Id + "' without assemblies=");
            if (!kv.TryGetValue("tier", out var tier) || !Enum.TryParse(tier, true, out v.Tier))
                Error(line, "vendor '" + v.Id + "' needs tier=foundation|presentation|sdk");
            if (v.Tier == VendorTier.Sdk && string.IsNullOrEmpty(v.Adapter))
                Error(line, "sdk vendor '" + v.Id + "' needs adapter=<assembly>");
            if (string.IsNullOrEmpty(v.Source) == string.IsNullOrEmpty(v.Package))
                Error(line, "vendor '" + v.Id + "' needs exactly one of source=<folder> or package=<name>");
            Vendors.Add(v);
        }

        private void ParseException(Dictionary<string, string> kv, int line)
        {
            var e = new ExceptionPolicy { Line = line };
            kv.TryGetValue("rule", out e.Rule);
            kv.TryGetValue("assembly", out e.Assembly);
            kv.TryGetValue("owner", out e.Owner);
            kv.TryGetValue("reason", out e.Reason);
            if (kv.TryGetValue("expires", out var exp) &&
                DateTime.TryParseExact(exp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
                e.Expires = d;
            Exceptions.Add(e);
        }

        private void Error(int line, string message) =>
            ParseErrors.Add(new RuleViolation("ARCH093", FileName, line, message));

        public VendorPolicy VendorOf(string assembly) =>
            Vendors.FirstOrDefault(v => v.Assemblies.Contains(assembly));
    }
}
