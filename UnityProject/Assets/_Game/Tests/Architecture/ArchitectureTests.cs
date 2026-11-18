using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>
    /// Runs every static rule of the shared rule engine (Assets/_Game/Architecture/Rules) over the project,
    /// one test case per rule ID, so a failure names the rule. The same engine runs in hooks, pre-commit,
    /// TestGate and the Unity Play Mode / build gates.
    /// If one of these fails, fix the code/asset. Never relax a rule without the owners' approval.
    /// </summary>
    public sealed class ArchitectureTests
    {
        private static string _root;
        private static List<RuleViolation> _violations;

        private static IEnumerable<string> StaticRuleIds =>
            RuleCatalog.All.Where(r => r.IsStatic).Select(r => r.Id);

        [OneTimeSetUp]
        public void Scan()
        {
            _root = FindRoot();
            _violations = ArchitectureScanner.ScanAll(_root);
        }

        [TestCaseSource(nameof(StaticRuleIds))]
        public void Rule(string ruleId)
        {
            var mine = _violations.Where(v => v.RuleId == ruleId).ToList();
            var info = RuleCatalog.All.First(r => r.Id == ruleId);
            Assert.That(mine, Is.Empty, ruleId + " " + info.Title + "\n" + ArchitectureScanner.Format(mine));
        }

        [Test]
        public void EveryViolation_HasAKnownRuleId()
        {
            var known = new HashSet<string>(RuleCatalog.All.Select(r => r.Id));
            Assert.That(_violations.Where(v => !known.Contains(v.RuleId)).Select(v => v.ToString()), Is.Empty);
        }

        [Test]
        public void EveryRule_IsDocumented()
        {
            // Keeps Docs/RULES.md in sync: agents read one section on demand instead of the whole rulebook.
            string docs = File.ReadAllText(Path.Combine(_root, "docs", "RULES.md"));
            var missing = RuleCatalog.All.Where(r => !docs.Contains("## " + r.Id)).Select(r => r.Id).ToList();
            Assert.That(missing, Is.Empty, "Add a '## ARCHxxx' section to Docs/RULES.md for each rule");
        }

        private static string FindRoot()
        {
            try
            {
                return SourceText.FindRepoRoot(TestContext.CurrentContext.TestDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                return SourceText.FindRepoRoot(Directory.GetCurrentDirectory());
            }
        }
    }
}
