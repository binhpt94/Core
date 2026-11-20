using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    public enum DependencyKind
    {
        /// <summary>Needs the other feature DONE (verified). Never skipped, not even by rush.</summary>
        Hard,
        /// <summary>Needs only an interface. Normal: provider done. Rush: interface frozen (in a PublicApi.txt) → allowed, with debt.</summary>
        Contract,
        /// <summary>Preferred order only.</summary>
        Soft
    }

    public sealed class PlanDependency
    {
        public DependencyKind Kind { get; set; }
        public string Feature { get; set; }
        /// <summary>Interface name for contract dependencies, e.g. IBestScoreService.</summary>
        public string Contract { get; set; }
        public int Line { get; set; }
    }

    public sealed class PlanRequirement
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Feature { get; set; }
        public HashSet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal);
        public int Line { get; set; }

        /// <summary>Verified by a human (`roadmap accept`), not by tests: looks, feel, store review...</summary>
        public bool IsManual => Tags.Contains("manual") || Tags.Contains("feasibility");

        /// <summary>Which branch states it: story (experience goal), art, or gameplay (default).</summary>
        public string Discipline => Tags.Contains("story") ? "story" : Tags.Contains("art") || Tags.Contains("feasibility") ? "art" : "gameplay";

        /// <summary>"Can art deliver this?" accepted by a human BEFORE deep gameplay work on an [art-risk] feature.</summary>
        public bool IsFeasibility => Tags.Contains("feasibility");

        /// <summary>
        /// High-risk behaviour (simulation rules, money, saved data): its tests go through separation of duties (seal by a
        /// tests session, code by an impl session, disputes by an arbiter). Other requirements count when their tests pass.
        /// </summary>
        public bool IsCritical => Tags.Contains(CriticalTag);
        public const string CriticalTag = "critical";

        /// <summary>
        /// Content hash: any change to the text or tags makes earlier verification STALE. [critical] is left out: it changes
        /// the process, not the behaviour, so tagging a requirement neither makes it STALE nor voids its seal.
        /// </summary>
        public string Hash => PlanText.Hash(Id + "|" + Text + "|" + string.Join(",", Tags.Where(t => t != CriticalTag).OrderBy(t => t, StringComparer.Ordinal)));
    }

    public sealed class PlanFeature
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Priority { get; set; } = "P2";
        public List<string> Modules { get; } = new List<string>();
        /// <summary>Art slots this feature shows ("Feature.Name" ids from the *ArtSlots catalogs).</summary>
        public List<string> Slots { get; } = new List<string>();
        /// <summary>Art level the feature needs to be DONE: graybox | candidate | approved | final (null = no art requirement).</summary>
        public string ArtLevel { get; set; }
        public HashSet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal);
        public List<string> Description { get; } = new List<string>();
        public List<PlanRequirement> Requirements { get; } = new List<PlanRequirement>();
        public List<PlanDependency> Dependencies { get; } = new List<PlanDependency>();
        public int Line { get; set; }

        public bool Rush => Tags.Contains("rush");
        public bool Deferred => Tags.Contains("deferred");
        public bool Dropped => Tags.Contains("dropped");
        public bool Active => !Deferred && !Dropped;
        /// <summary>Art might not be feasible: gameplay requirements wait until its [feasibility] requirements are accepted.</summary>
        public bool ArtRisk => Tags.Contains("art-risk");
        public int PriorityRank => Priority.Length == 2 && char.IsDigit(Priority[1]) ? Priority[1] - '0' : 9;
    }

    public sealed class Plan
    {
        public string Title { get; set; }
        public List<PlanFeature> Features { get; } = new List<PlanFeature>();
        public List<string> Errors { get; } = new List<string>();

        public PlanFeature Feature(string id) => Features.FirstOrDefault(f => f.Id == id);

        public PlanRequirement Requirement(string id) => Features.SelectMany(f => f.Requirements).FirstOrDefault(r => r.Id == id);

        public IEnumerable<PlanRequirement> AllRequirements => Features.SelectMany(f => f.Requirements);
    }

    /// <summary>
    /// Docs/Plan/features.md: the product plan, OWNED BY THE HUMAN (agents propose in Docs/Plan/Inbox/). Markdown a person edits by hand,
    /// parsed strictly: anything it can't read is an error with a line number (ARCH110), never a guess.
    ///
    ///   # Plan: Quiz Defense
    ///   ## F-TIMER  Đếm giờ mỗi câu  [P0] [modules: MathQuiz] [rush]
    ///   Free description lines.
    ///   - R-TIMER-1  Hết giờ đúng tick hạn chót
    ///   - R-TIMER-3  3 giây cuối hiển thị khẩn cấp  [manual]
    ///   deps: hard F-QUIZ-CORE · contract F-BEST-SCORE (IBestScoreService) · soft F-ARENA
    ///
    /// Feature tags: [P0..P3] [modules: A, B] [rush] [deferred] [dropped]. Requirement tags: [manual] [story] [art] [feasibility] [critical].
    /// There are NO tasks here: tasks are derived from gaps when work starts (nothing to forget, nothing to go stale).
    /// </summary>
    public static class PlanParser
    {
        public const string RelativePath = "Docs/Plan/features.md";
        public const string InboxFolder = "Docs/Plan/Inbox/";
        public const string AcceptedPath = "Docs/Plan/accepted.txt";

        private static readonly Regex FeatureLine = new Regex(@"^##\s+(?<id>F-[A-Z0-9][A-Z0-9-]*)\s+(?<rest>.+)$");
        private static readonly Regex RequirementLine = new Regex(@"^-\s+(?<id>R-[A-Z0-9][A-Z0-9-]*)\s+(?<rest>.+)$");
        private static readonly Regex TagAtEnd = new Regex(@"\s*\[(?<tag>[^\[\]]+)\]\s*$");
        private static readonly Regex DepItem = new Regex(@"^(?<kind>hard|contract|soft)\s+(?<id>F-[A-Z0-9][A-Z0-9-]*)(\s*\((?<iface>[A-Za-z_][A-Za-z0-9_.]*)\))?$");
        private static readonly HashSet<string> FeatureTags = new HashSet<string> { "rush", "deferred", "dropped", "art-risk" };
        private static readonly HashSet<string> RequirementTags = new HashSet<string> { "manual", "story", "art", "feasibility", "critical" };
        /// <summary>graybox &lt; candidate (bound, passes the automatic art gate) &lt; final (owner `art accept`). Art debt never blocks dev.</summary>
        public static readonly string[] ArtLevels = { "graybox", "candidate", "final" };

        /// <summary>The legacy level "approved" (before acceptance moved to finals only) reads as candidate.</summary>
        public static string NormalizeArtLevel(string level) => level == "approved" ? "candidate" : level;


        public static Plan Load(string repoRoot)
        {
            string path = Path.Combine(repoRoot, RelativePath);
            var plan = File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
            CrLog.AddTo(plan, repoRoot); // approved change requests (Docs/Plan/CRs) are requirements of their target feature
            return plan;
        }

        public static Plan Parse(string text)
        {
            var plan = new Plan();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            PlanFeature current = null;
            var ids = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Length; i++)
            {
                int n = i + 1;
                string line = lines[i].TrimEnd();
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("<!--", StringComparison.Ordinal))
                    continue;

                if (t.StartsWith("# ", StringComparison.Ordinal))
                {
                    plan.Title = t.Substring(2).Trim();
                    continue;
                }

                if (t.StartsWith("##", StringComparison.Ordinal))
                {
                    var m = FeatureLine.Match(t);
                    if (!m.Success)
                    {
                        plan.Errors.Add("line " + n + ": feature heading must be '## F-ID  Title  [P0] [modules: X]': " + t);
                        current = null;
                        continue;
                    }
                    current = new PlanFeature { Id = m.Groups["id"].Value, Line = n };
                    var title = SplitTags(m.Groups["rest"].Value, out var tags);
                    current.Title = title;
                    foreach (var tag in tags)
                    {
                        if (Regex.IsMatch(tag, "^P[0-3]$"))
                            current.Priority = tag;
                        else if (tag.StartsWith("modules:", StringComparison.Ordinal))
                            current.Modules.AddRange(tag.Substring(8).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                        else if (tag.StartsWith("slots:", StringComparison.Ordinal))
                            current.Slots.AddRange(tag.Substring(6).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                        else if (tag.StartsWith("art:", StringComparison.Ordinal))
                        {
                            current.ArtLevel = NormalizeArtLevel(tag.Substring(4).Trim());
                            if (Array.IndexOf(ArtLevels, current.ArtLevel) < 0)
                                plan.Errors.Add("line " + n + ": [art: " + current.ArtLevel + "] must be one of " + string.Join(", ", ArtLevels));
                        }
                        else if (FeatureTags.Contains(tag))
                            current.Tags.Add(tag);
                        else
                            plan.Errors.Add("line " + n + ": unknown feature tag [" + tag + "] (allowed: P0-P3, modules: X, slots: A.B, art: level, rush, deferred, dropped, art-risk)");
                    }
                    if (title.Length == 0)
                        plan.Errors.Add("line " + n + ": feature " + current.Id + " has no title");
                    if (!ids.Add(current.Id))
                        plan.Errors.Add("line " + n + ": duplicate id " + current.Id);
                    plan.Features.Add(current);
                    continue;
                }

                if (current == null)
                {
                    if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("deps:", StringComparison.Ordinal))
                        plan.Errors.Add("line " + n + ": requirement/deps outside a feature");
                    continue; // preamble text is allowed
                }

                if (t.StartsWith("- ", StringComparison.Ordinal))
                {
                    var m = RequirementLine.Match(t);
                    if (!m.Success)
                    {
                        plan.Errors.Add("line " + n + ": requirement must be '- R-ID  Text  [manual]': " + t);
                        continue;
                    }
                    var r = new PlanRequirement { Id = m.Groups["id"].Value, Feature = current.Id, Line = n };
                    r.Text = SplitTags(m.Groups["rest"].Value, out var tags);
                    foreach (var tag in tags)
                        if (RequirementTags.Contains(tag))
                            r.Tags.Add(tag);
                        else
                            plan.Errors.Add("line " + n + ": unknown requirement tag [" + tag + "] (allowed: manual, story, art, feasibility, critical)");
                    if (r.Text.Length == 0)
                        plan.Errors.Add("line " + n + ": requirement " + r.Id + " has no text");
                    if (!ids.Add(r.Id))
                        plan.Errors.Add("line " + n + ": duplicate id " + r.Id);
                    current.Requirements.Add(r);
                    continue;
                }

                if (t.StartsWith("deps:", StringComparison.Ordinal))
                {
                    foreach (var item in t.Substring(5).Split(new[] { '·', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
                    {
                        var m = DepItem.Match(item);
                        if (!m.Success)
                        {
                            plan.Errors.Add("line " + n + ": dependency must be 'hard F-X', 'contract F-X (IInterface)' or 'soft F-X': " + item);
                            continue;
                        }
                        var kind = m.Groups["kind"].Value == "hard" ? DependencyKind.Hard : m.Groups["kind"].Value == "contract" ? DependencyKind.Contract : DependencyKind.Soft;
                        if (kind == DependencyKind.Contract && !m.Groups["iface"].Success)
                            plan.Errors.Add("line " + n + ": contract dependency needs the interface: contract " + m.Groups["id"].Value + " (IName)");
                        current.Dependencies.Add(new PlanDependency { Kind = kind, Feature = m.Groups["id"].Value, Contract = m.Groups["iface"].Success ? m.Groups["iface"].Value : null, Line = n });
                    }
                    continue;
                }

                current.Description.Add(t);
            }
            return plan;
        }

        private static string SplitTags(string rest, out List<string> tags)
        {
            tags = new List<string>();
            string s = rest.Trim();
            Match m;
            while ((m = TagAtEnd.Match(s)).Success)
            {
                tags.Insert(0, m.Groups["tag"].Value.Trim());
                s = s.Substring(0, m.Index).TrimEnd();
            }
            return s.Trim();
        }
    }

    public static class PlanText
    {
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 6).Replace("-", "").ToLowerInvariant();
        }
    }
}
