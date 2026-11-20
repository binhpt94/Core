using System.Linq;
using NUnit.Framework;
using TestGate.DeadCode;

namespace LogicTests.AgentControl
{
    /// <summary>ARCH033: code nothing references is flagged in the files a change touches; what Unity or a runner calls is not.</summary>
    public sealed class DeadCodeTests
    {
        private const string File = "UnityProject/Assets/_Game/Features/A/Logic/Rules.cs";

        private static string[] Found(string code, string other = "", string asset = "", string tool = "")
        {
            var index = new DeadCodeScan.Index();
            index.AddSource(File, code);
            index.AddSource("UnityProject/Assets/_Game/Features/A/Logic/Caller.cs", other);
            index.AddAsset(asset);
            index.AddTool(tool);
            return DeadCodeScan.Check(index, new[] { File }).Select(v => v.Message.Split('\'')[1]).ToArray();
        }

        [Test]
        public void AMemberNobodyCalls_IsFlagged_OneThatIsCalled_IsNot()
        {
            const string code = "class Rules {\n  internal int Score() => Bonus();\n  private int Bonus() => 2;\n  private int ForLater() => 3;\n}\n";
            Assert.That(Found(code, "class Caller { int X(Rules r) => r.Score(); }"), Is.EqualTo(new[] { "ForLater" }));
            Assert.That(Found(code), Is.EquivalentTo(new[] { "Rules", "Score", "ForLater" }), "a type only its own file names is dead too");
        }

        [Test]
        public void WhatUnityOrARunnerCallsByItself_IsNotFlagged()
        {
            const string code = "class Rules : MonoBehaviour {\n  void Awake() { }\n  [SerializeField] int speed;\n  public void OnPlayClicked() { }\n" +
                                "  [Test] public void Works() { }\n  public override string ToString() => \"\";\n  public static void Bake() { }\n}\n" +
                                "class Post : AssetPostprocessor { void OnPreprocessTexture() { } }\n" +
                                "class Gate : IPreprocessBuildWithReport { public int callbackOrder => 0; }\n";
            Assert.That(Found(code, asset: "speed: 3\nm_MethodName: OnPlayClicked\n", tool: "-executeMethod Game.A.Rules.Bake"), Is.Empty);
            Assert.That(Found(code), Is.EquivalentTo(new[] { "speed", "OnPlayClicked", "Bake" }), "without the asset/tool mention they are dead");
        }

        [Test]
        public void TheRuleEngineAndGeneratedCode_AreNotJudged()
        {
            Assert.That(DeadCodeScan.Judged("UnityProject/Assets/_Game/Architecture/Rules/X.cs"), Is.False);
            Assert.That(DeadCodeScan.Judged("UnityProject/Assets/_Game/Features/A/Generated/X.cs"), Is.False);
            Assert.That(DeadCodeScan.Judged(File), Is.True);
        }
    }
}
