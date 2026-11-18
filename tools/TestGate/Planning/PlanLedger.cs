using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    public sealed class LedgerEntry
    {
        /// <summary>Requirement content hash when it was last verified (by tests or by a human).</summary>
        public string RequirementHash { get; set; }
        /// <summary>Hash of the covering test files at that moment.</summary>
        public string TestsHash { get; set; }
        public string VerifiedUtc { get; set; }
        public string By { get; set; }            // "tests" | "human"
    }

    /// <summary>
    /// What each requirement looked like when it was last verified. A requirement whose text changed since then is STALE:
    /// re-running unchanged tests does NOT clear it (they test the old wording); touching a covering test or a human
    /// `plan accept` does. Written only by the gate (after a green dotnet run) and by `plan accept`.
    /// </summary>
    public static class PlanLedger
    {
        public static string PathOf(string root) => Path.Combine(root, ".agent", "plan-ledger.json");

        public static Dictionary<string, LedgerEntry> Load(string root)
        {
            string p = PathOf(root);
            if (!File.Exists(p))
                return new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, LedgerEntry>>(File.ReadAllText(p)) ?? new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        }

        public static void Save(string root, Dictionary<string, LedgerEntry> ledger)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(root)));
            File.WriteAllText(PathOf(root), JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Hash of the test files that cover a requirement (content, so a touched test counts as "updated").</summary>
        public static string TestsHash(string root, IEnumerable<CoverEntry> covers) =>
            PlanText.Hash(string.Join("|", covers.Select(c => c.File).Distinct().OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => f + ":" + (File.Exists(Path.Combine(root, f)) ? PlanText.Hash(File.ReadAllText(Path.Combine(root, f))) : "-"))));

        /// <summary>After a green gate: record every requirement whose tests all passed (unless its new wording hasn't reached the tests yet).</summary>
        public static int RecordVerified(string root, Plan plan, IReadOnlyList<CoverEntry> covers, TestResultsIndex results)
        {
            var ledger = Load(root);
            int changed = 0;
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            foreach (var r in plan.AllRequirements.Where(x => !x.IsManual))
            {
                var mine = covers.Where(c => c.Requirement == r.Id).ToList();
                if (mine.Count == 0)
                    continue;
                var outcomes = mine.SelectMany(c => results.Find(c.TestKey)).ToList();
                if (outcomes.Count == 0 || outcomes.Any(o => !o.Passed) || mine.Any(c => results.Find(c.TestKey).Count == 0))
                    continue;
                string tests = TestsHash(root, mine);
                if (ledger.TryGetValue(r.Id, out var e) && e.RequirementHash != r.Hash && e.TestsHash == tests)
                    continue; // wording changed, tests untouched: stays STALE
                if (e != null && e.RequirementHash == r.Hash && e.TestsHash == tests)
                    continue;
                ledger[r.Id] = new LedgerEntry { RequirementHash = r.Hash, TestsHash = tests, VerifiedUtc = now, By = "tests" };
                changed++;
            }
            if (changed > 0)
                Save(root, ledger);
            return changed;
        }

        public static void RecordHuman(string root, PlanRequirement r, IReadOnlyList<CoverEntry> covers)
        {
            var ledger = Load(root);
            ledger[r.Id] = new LedgerEntry
            {
                RequirementHash = r.Hash, TestsHash = TestsHash(root, covers.Where(c => c.Requirement == r.Id)),
                VerifiedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), By = "human"
            };
            Save(root, ledger);
        }
    }
}
