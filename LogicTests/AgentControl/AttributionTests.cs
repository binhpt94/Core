using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    /// <summary>A red gate blocks the agent only for what the agent did; a human's edits go to the human, with human commands.</summary>
    public sealed class AttributionTests
    {
        private static AgentEvent E(string type, string path, string session) =>
            new AgentEvent { Type = type, Session = session, Data = { ["path"] = path } };

        private static readonly Dictionary<string, string> Authors = Attribution.Authors(new[]
        {
            E(EventTypes.FileModified, "Assets/_Game/Features/A/Logic/Agent.cs", "s1"),
            E(EventTypes.UnobservedChange, "plan/features.md", "s1"),
            E(EventTypes.FileModified, "plan/inbox/p.md", "s1"),
            E(EventTypes.FileModified, "Assets/_Game/Features/A/Logic/Both.cs", "s1"),
            E(EventTypes.UnobservedChange, "Assets/_Game/Features/A/Logic/Both.cs", "s1"), // a human edited it after the agent
        });

        private static RuleViolation V(string id, string path, int line = 0) => new RuleViolation(id, path, line, "msg");

        [Test]
        public void LastObservedWriterIsTheAuthor_UnknownMeansHuman()
        {
            Assert.That(Attribution.AuthorOf("Assets/_Game/Features/A/Logic/Agent.cs", Authors), Is.EqualTo("agent"));
            Assert.That(Attribution.AuthorOf("Assets/_Game/Features/A/Logic/Both.cs", Authors), Is.EqualTo("human"));
            Assert.That(Attribution.AuthorOf("never/seen.cs", Authors), Is.EqualTo("human"));
        }

        [Test]
        public void Violations_AreOwnedByWhoeverChangedThatFile()
        {
            var changed = new[] { "Assets/_Game/Features/A/Logic/Agent.cs", "plan/features.md" };
            var fixes = Attribution.Classify(new[] { V("ARCH110", "plan/features.md", 68), V("ARCH050", "Assets/_Game/Features/A/Logic/Agent.cs", 3) },
                new string[0], "G0", changed, Authors);
            Assert.That(fixes.Single(f => f.Problem.StartsWith("ARCH110")).Who, Is.EqualTo("human"));
            Assert.That(fixes.Single(f => f.Problem.StartsWith("ARCH110")).Hint, Does.Contain("dòng 68").And.Contain("git checkout -- plan/features.md"));
            Assert.That(fixes.Single(f => f.Problem.StartsWith("ARCH050")).Who, Is.EqualTo("agent"));
        }

        [Test]
        public void AnUnchangedAssetRuleBreak_IsTheHumans_WhenTheAgentTouchedNoAssets()
        {
            // e.g. ARCH132 in Boss.prefab after the human moved Wolf.fbx without its .meta; the agent only edited code.
            var fixes = Attribution.Classify(new[] { V("ARCH132", "Assets/_Game/Features/A/Assets/Private/Boss.prefab", 2) }, new string[0], "G0",
                new[] { "Assets/_Game/Features/A/Logic/Agent.cs" }, Authors);
            Assert.That(fixes.Single().Who, Is.EqualTo("human"));
        }

        [Test]
        public void FailingTests_AreTheAgents_IfItChangedCode_ElseTheHumans()
        {
            var human = Attribution.Classify(new RuleViolation[0], new[] { "GoldenReplay_StillMatches(\"afk\")" }, "G1",
                new[] { "Assets/_Game/Features/A/Logic/Both.cs" }, Authors).Single();
            Assert.That(human.Who, Is.EqualTo("human"));
            StringAssert.Contains("GOLDEN_WRITE=1", human.Hint);
            StringAssert.Contains("git checkout -- Assets/_Game/Features/A/Logic/Both.cs", human.Hint);

            var mixed = Attribution.Classify(new RuleViolation[0], new[] { "SomeTest" }, "G1",
                new[] { "Assets/_Game/Features/A/Logic/Both.cs", "Assets/_Game/Features/A/Logic/Agent.cs" }, Authors).Single();
            Assert.That(mixed.Who, Is.EqualTo("agent"), "both changed code: we can't prove it isn't the agent's, so the agent must look");

            var nobody = Attribution.Classify(new RuleViolation[0], new[] { "SomeTest" }, "G1", new string[0], Authors).Single();
            Assert.That(nobody.Who, Is.EqualTo("agent"), "nobody else changed code either: never hand an unexplained red to the human by default");
        }

        [Test]
        public void GeneratedDocs_AreFixedByWhoeverChangedTheCode()
        {
            var v = new[] { V("ARCH090", "docs/architecture/overview.md") };
            Assert.That(Attribution.Classify(v, new string[0], "G0", new[] { "Assets/_Game/Features/A/Logic/Agent.cs" }, Authors).Single().Who, Is.EqualTo("agent"));
            Assert.That(Attribution.Classify(v, new string[0], "G0", new[] { "plan/features.md" }, Authors).Single().Who, Is.EqualTo("human"));

            // the human added art; the agent only edited a comment: the docs are stale because of the assets → the human's
            var assets = new[] { new RuleViolation("ARCH090", "docs/architecture/overview.md", 0, "stale vs source. Changes: -| Assets (Public/Private) | 0/0 | +| Assets (Public/Private) | 1/2 |") };
            Assert.That(Attribution.Classify(assets, new string[0], "G0", new[] { "Assets/_Game/Features/A/Logic/Agent.cs" }, Authors).Single().Who, Is.EqualTo("human"));
        }
    }
}
