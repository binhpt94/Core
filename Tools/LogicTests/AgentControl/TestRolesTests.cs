using System.Linq;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    /// <summary>Who may write what: the tests session writes tests (+ skeleton API), the impl session writes code, never tests.</summary>
    public sealed class TestRolesTests
    {
        private const string F = "UnityProject/Assets/_Game/Features/A/";

        private static AgentTask Task(string role)
        {
            var t = ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now");
            t.Role = role;
            return t;
        }

        [Test]
        public void TheTestsRole_WritesTestsAndSkeleton_NeverOtherCode()
        {
            var t = Task(TestRoles.Tests);
            Assert.That(ScopePolicy.Check(t, F + "Tests/CoinTests.cs").Allowed, Is.True);
            Assert.That(ScopePolicy.Check(t, F + "Logic/Domain/Coins.cs").Allowed, Is.True, "skeleton only: its content is checked after the write");
            Assert.That(ScopePolicy.Check(t, F + "Contracts/ICoins.cs").Allowed, Is.True);
            Assert.That(ScopePolicy.Check(t, F + "Unity/CoinView.cs").Allowed, Is.False);
            Assert.That(ScopePolicy.Check(t, F + "Assets/Private/Coin.prefab").Allowed, Is.False);
        }

        [Test]
        public void TheImplRole_WritesCode_NeverTests()
        {
            var t = Task(TestRoles.Impl);
            Assert.That(ScopePolicy.Check(t, F + "Logic/Domain/Coins.cs").Allowed, Is.True);
            Assert.That(ScopePolicy.Check(t, F + "Unity/CoinView.cs").Allowed, Is.True);
            var d = ScopePolicy.Check(t, F + "Tests/CoinTests.cs");
            Assert.That(d.Allowed, Is.False);
            Assert.That(d.Reason, Does.Contain("dispute"));
        }

        [Test]
        public void NobodyWritesTheSealsByHand_AndOldTasksKeepTheirScope()
        {
            Assert.That(ScopePolicy.Check(Task(TestRoles.Tests), "Docs/Plan/Seals/A.txt").Allowed, Is.False);
            Assert.That(ScopePolicy.Check(Task(null), F + "Tests/CoinTests.cs").Allowed, Is.True, "no role = old behaviour (its requirements just can't be VERIFIED without a seal)");
            Assert.That(ScopePolicy.Check(Task(null), F + "Logic/Domain/Coins.cs").Allowed, Is.True);
        }

        [Test]
        public void Skeleton_AllowsSignaturesWithDefaultBodies_NotLogic()
        {
            const string before = "namespace G\n{\n    internal sealed class Coins\n    {\n    }\n}\n";
            const string skeleton = "namespace G\n{\n    internal sealed class Coins\n    {\n        public int Total { get; private set; }\n        /// <summary>Coins for a round.</summary>\n" +
                                    "        public int Reward(int correct, bool won) => default;\n        public bool Save()\n        {\n            return default;\n        }\n        public void Reset() { }\n    }\n" +
                                    "    public interface ICoins\n    {\n        int Total { get; }\n    }\n}\n";
            Assert.That(TestRoles.SkeletonViolations(before, skeleton), Is.Empty);
            Assert.That(TestRoles.SkeletonViolations(null, skeleton), Is.Empty, "a new file can be skeleton too");

            var logic = skeleton.Replace("=> default;", "=> won ? 5 + 2 * correct : 0;");
            Assert.That(TestRoles.SkeletonViolations(before, logic), Has.Some.Contains("won ? 5"));
            var statement = skeleton.Replace("return default;", "var x = Load();\n            return x;");
            Assert.That(TestRoles.SkeletonViolations(before, statement), Is.Not.Empty);
            var thrower = skeleton.Replace("=> default;", "=> throw new System.NotImplementedException();");
            Assert.That(TestRoles.SkeletonViolations(before, thrower), Is.Not.Empty, "throwing makes every test red for the wrong reason");
            const string existing = "namespace G\n{\n    internal sealed class Coins\n    {\n        public int Reward() => 7;\n    }\n}\n";
            Assert.That(TestRoles.SkeletonViolations(existing, existing.Replace("=> 7;", "=> default;")), Is.Not.Empty, "the tests role never removes real code");
        }

        [Test]
        public void Parallel_TestsAndImplOfOneFeatureMayRunTogether_TwoWritersOfTheSameFolderMayNot()
        {
            Assert.That(TestRoles.Conflict(TestRoles.Tests, TestRoles.Impl), Is.False);
            Assert.That(TestRoles.Conflict(TestRoles.Arbiter, TestRoles.Impl), Is.False);
            Assert.That(TestRoles.Conflict(TestRoles.Impl, TestRoles.Impl), Is.True);
            Assert.That(TestRoles.Conflict(TestRoles.Tests, TestRoles.Arbiter), Is.True);
            Assert.That(TestRoles.Conflict(null, TestRoles.Tests), Is.True, "a task without a role writes everything");
        }
    }
}
