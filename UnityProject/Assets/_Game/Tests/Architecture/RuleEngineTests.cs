using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>
    /// Proves every static rule actually fires (positive fixture) and doesn't fire on valid code (negative).
    /// CoverageTest fails if a static rule has no positive fixture, so a new rule cannot ship untested.
    /// </summary>
    public sealed class RuleEngineTests
    {
        private static readonly HashSet<string> Fired = new HashSet<string>();

        // ---------- source rules ----------

        private const string Logic = "UnityProject/Assets/_Game/Features/A/Logic/X.cs";
        private const string UnityLayer = "UnityProject/Assets/_Game/Features/A/Unity/X.cs";
        private const string Boot = "UnityProject/Assets/_Game/Bootstrap/X.cs";
        private const string CoreFile = "UnityProject/Assets/_Game/Core/Runtime/X.cs";
        private const string TestFile = "UnityProject/Assets/_Game/Features/A/Tests/X.cs";

        private static List<RuleViolation> Src(string path, string code)
        {
            var v = SourceRules.Check(path, code).ToList();
            foreach (var x in v)
                Fired.Add(x.RuleId);
            return v;
        }

        private static void Fires(string id, string path, string code) =>
            Assert.That(Src(path, code).Select(v => v.RuleId), Does.Contain(id), code);

        private static void Silent(string id, string path, string code) =>
            Assert.That(Src(path, code).Where(v => v.RuleId == id).Select(v => v.ToString()), Is.Empty, code);

        [TestCase("ARCH040", "class X { void F() { SendMessage(\"Add\", 1); } }")]
        [TestCase("ARCH040", "class X { void F() { Invoke(\"Start\", 1f); } }")]
        [TestCase("ARCH040", "class X { void F() { StartCoroutine(nameof(Run)); } }")]
        [TestCase("ARCH041", "class X { void F() { var t = Type.GetType(\"Game.Y\"); } }")]
        [TestCase("ARCH041", "class X { void F() { typeof(Y).GetMethod(\"Jump\"); } }")]
        [TestCase("ARCH041", "class X { void F(object o) { dynamic d = o; } }")]
        [TestCase("ARCH042", "class X { void F() { GameObject.Find(\"Player\"); } }")]
        [TestCase("ARCH042", "class X { void F() { transform.Find(\"Child\"); } }")]
        [TestCase("ARCH042", "class X { void F() { Resources.Load<Sprite>(\"coin\"); } }")]
        [TestCase("ARCH043", "class X { void F() { FindObjectOfType<Y>(); } }")]
        [TestCase("ARCH043", "class X { public static X Instance { get; private set; } }")]
        [TestCase("ARCH044", "class X { void F() { animator.SetTrigger(\"Win\"); } }")]
        [TestCase("ARCH053", "class X { void F() { Work().Forget(); } }")]
        [TestCase("ARCH030", "public partial class GameManager { }")]
        [TestCase("ARCH054", "namespace Game.A.Application { class X { } }")]
        public void UnityLayer_Fires(string id, string code) => Fires(id, UnityLayer, code);

        [TestCase("ARCH040", "class X { void F() { handler?.Invoke(1); OnDone.Invoke(); } }")]
        [TestCase("ARCH043", "class X { void F() { FindObjectOfType<Y>(); } }", Boot)]
        [TestCase("ARCH043", "class X { public static readonly X Instance = new X(); }", CoreFile)]
        [TestCase("ARCH044", "class X { static readonly int Win = Animator.StringToHash(\"Win\"); void F() { animator.SetTrigger(Win); } }")]
        [TestCase("ARCH041", "class X { void F() { SendMessage(\"x\"); typeof(Y).GetMethod(\"Jump\"); } }", TestFile)]
        [TestCase("ARCH053", "class X { void F() { Work().Forget(); } }", "UnityProject/Assets/_Game/Core/Runtime/Lifetime.cs")]
        [TestCase("ARCH041", "class X { void F() { typeof(Y).GetField(\"x\"); } }", "UnityProject/Assets/_Game/Bootstrap/Editor/X.cs")]
        [TestCase("ARCH040", "// SendMessage(\"x\") in a comment\nclass X { }")]
        public void Valid_IsSilent(string id, string code, string path = UnityLayer) => Silent(id, path, code);

        [Test]
        public void PureCode_Rules()
        {
            Fires("ARCH050", Logic, "using UnityEngine;\nclass X { }");
            Silent("ARCH050", UnityLayer, "using UnityEngine;\nclass X { }");
            Fires("ARCH051", Logic, "class X { int F() => new System.Random().Next(); }");
            Silent("ARCH051", CoreFile, "class X { System.Random r = new System.Random(); }");
            Fires("ARCH052", Logic, "class X { async UniTask F() { await UniTask.Delay(1); } }");
            Silent("ARCH052", UnityLayer, "class X { UniTask F() => UniTask.Delay(1); }");
        }

        [Test]
        public void Determinism_Rules()
        {
            Fires("ARCH055", Logic, "class X { int F(string s) => s.GetHashCode(); }");
            Fires("ARCH055", "UnityProject/Assets/_Game/Core/Runtime/X.cs", "class X { int F(int a, int b) => HashCode.Combine(a, b); }");
            Silent("ARCH055", UnityLayer, "class X { int F(string s) => s.GetHashCode(); }");

            Fires("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/Simulation/S.cs", "class S { float speed; }");
            Fires("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/Domain/D.cs", "class D { int F(int x) => (int)Math.Round(x / 3.0); }");
            Fires("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/Simulation/S.cs", "class S { int F(int s) => (int)(s * (175 / 100f)); }"); // literal, no keyword
            Fires("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/Simulation/S.cs", "class S { int F(int s) => (int)(s * 1.75); }");
            Silent("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/Simulation/S.cs", "class S { const int Rate = 20; long T = 1_000_000; int F(int s) => s * 175 / 100; }");
            Silent("ARCH056", "UnityProject/Assets/_Game/Features/A/Logic/UseCases/P.cs", "class P { float Fraction; }");

            Fires("ARCH057", UnityLayer, "using Game.A.Simulation;\nclass V : MonoBehaviour { }");
            Fires("ARCH057", UnityLayer, "class V : MonoBehaviour { Game.A.Domain.Question q; }");
            Silent("ARCH057", UnityLayer, "using Game.A.UseCases;\nclass V : MonoBehaviour, IAView { }");
            Silent("ARCH057", UnityLayer, "using Game.A.Domain;\nstatic class Installer { }");
        }

        [Test]
        public void Partial_GeneratedIsAllowedOnlyWithHeader()
        {
            Silent("ARCH030", "UnityProject/Assets/_Game/Features/A/Unity/Generated/X.g.cs", "// <auto-generated>\npartial class X { }");
            Fires("ARCH030", "UnityProject/Assets/_Game/Features/A/Unity/Generated/X.g.cs", "partial class X { }");
            Fires("ARCH030", "UnityProject/Assets/_Game/Features/A/Unity/X.Generated.cs", "// <auto-generated>\npartial class X { }");
        }

        [Test]
        public void FileSize_RuntimeOnly()
        {
            string longFile = string.Join("\n", Enumerable.Repeat("// line", SourceRules.MaxLinesPerFile + 5));
            Fires("ARCH031", Logic, longFile);
            Silent("ARCH031", TestFile, longFile);
        }

        [Test]
        public void InternalsVisibleTo_OnlySameFeature()
        {
            Silent("ARCH021", "UnityProject/Assets/_Game/Features/A/Logic/AssemblyInfo.cs", "[assembly: InternalsVisibleTo(\"Game.A.Unity\")]\n[assembly: InternalsVisibleTo(\"Game.A.Tests\")]");
            Fires("ARCH021", "UnityProject/Assets/_Game/Features/A/Logic/AssemblyInfo.cs", "[assembly: InternalsVisibleTo(\"Game.B.Unity\")]");
            Fires("ARCH021", "UnityProject/Assets/_Game/Features/A/Logic/AssemblyInfo.cs", "[assembly: InternalsVisibleTo(\"Game.Bootstrap\")]");
        }

        [Test]
        public void SerializedConfig_OnMonoBehaviour()
        {
            Fires("ARCH070", UnityLayer, "class V : MonoBehaviour { [SerializeField] private int maxEnemies = 10; }");
            Fires("ARCH070", UnityLayer, "class V : MonoBehaviour { public float speed; }");
            Silent("ARCH070", UnityLayer, "class V : MonoBehaviour { [SerializeField, VisualTuning] private float fadeSeconds = 0.2f; }");
            Silent("ARCH070", UnityLayer, "class V : MonoBehaviour { [SerializeField] private Text label; public string Label => label.text; private int _count; }");
            Silent("ARCH070", UnityLayer, "class C : ScriptableObject { [SerializeField] private int maxEnemies = 10; }");
        }

        [Test]
        public void ScriptableObject_ReadOnly()
        {
            Fires("ARCH071", UnityLayer, "class C : ScriptableObject { public int MaxEnemies; }");
            Fires("ARCH071", UnityLayer, "class C : ScriptableObject { public int MaxEnemies { get; set; } }");
            Silent("ARCH071", UnityLayer, "class C : ScriptableObject { [SerializeField] private int max; public int Max => max; }");
        }

        // ---------- asset + asmdef rules on a throw-away project ----------

        private string _tmp;

        [SetUp]
        public void CreateTemp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "rule-engine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void DeleteTemp()
        {
            if (!Directory.Exists(_tmp))
                return;
            foreach (var f in Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git objects are read-only
            Directory.Delete(_tmp, true);
        }

        private void Write(string rel, string content)
        {
            string full = Path.Combine(_tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private void Asset(string rel, string guid, string content = "%YAML 1.1\n")
        {
            Write(rel, content);
            Write(rel + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        }

        private List<RuleViolation> ScanAll()
        {
            var v = ArchitectureScanner.ScanAll(_tmp);
            foreach (var x in v)
                Fired.Add(x.RuleId);
            return v;
        }

        private static string G(char c) => new string(c, 32);

        [Test]
        public void Assets_IsolationOverridesAndPlacement()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "EditorSettings:\n  m_SerializationMode: 2\n");
            Asset("UnityProject/Assets/_Game/Features/A/Assets/Private/a.prefab", G('a'));
            Asset("UnityProject/Assets/_Game/Features/A/Assets/Public/AEntry.asset", G('b'), "x: {fileID: 1, guid: " + G('a') + ", type: 2}\n");
            Asset("UnityProject/Assets/_Game/Features/B/Assets/Private/b.prefab", G('c'),
                "ok: {fileID: 1, guid: " + G('b') + ", type: 2}\nbad: {fileID: 1, guid: " + G('a') + ", type: 3}\n");
            Asset("UnityProject/Assets/_Game/Scenes/Main.unity", G('d'),
                "    - target: {fileID: 1}\n      propertyPath: m_LocalPosition.x\n" +
                "    - target: {fileID: 1}\n      propertyPath: maxEnemies\n" +
                "  m_Script: {fileID: 0}\n");
            Write("UnityProject/Assets/_Game/Features/A/Unity/coin.png", "png");
            Asset("UnityProject/Assets/_Game/Features/A/Assets/Private/Resources/x.asset", G('e'));
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Write("UnityProject/Assets/_Game/Features/B/README.md", "#");

            var v = ScanAll();
            string all = string.Join("\n", v);

            Assert.That(v.Count(x => x.RuleId == "ARCH080"), Is.EqualTo(1), all);
            Assert.That(v.Single(x => x.RuleId == "ARCH080").Path, Does.EndWith("B/Assets/Private/b.prefab"));
            Assert.That(v.Single(x => x.RuleId == "ARCH081").Message, Does.Contain("maxEnemies"));
            Assert.That(v.Count(x => x.RuleId == "ARCH073"), Is.EqualTo(1), all);
            Assert.That(v.Single(x => x.RuleId == "ARCH082").Path, Does.EndWith("Unity/coin.png"));
            Assert.That(v.Count(x => x.RuleId == "ARCH083"), Is.EqualTo(1), all);
            Assert.That(v.Any(x => x.RuleId == "ARCH084"), Is.False);

            var incoming = new AssetRules(_tmp).IncomingReferences("UnityProject/Assets/_Game/Features/A").ToList();
            Assert.That(incoming.Count, Is.EqualTo(1), string.Join("\n", incoming)); // b.prefab (scene has no A refs)
            Assert.That(incoming[0], Does.StartWith("UnityProject/Assets/_Game/Features/B/Assets/Private/b.prefab"));
        }

        [Test]
        public void ScanFiles_SkipsDeletedPaths_EvenAfterACaseOnlyRename()
        {
            Write("Tools/test-gate.sh", "#!/usr/bin/env bash\n");
            // git still lists the old spelling (deleted in the branch); on Windows/macOS File.Exists("tools/...") is true.
            var v = ArchitectureScanner.ScanFiles(_tmp, new[] { "tools/test-gate.sh", "Tools/test-gate.sh", "notes.txt" }, projectRules: false);
            Assert.That(v.Select(x => x.Path), Is.Empty, string.Join("\n", v));
            Assert.That(ArchitectureScanner.ExistsExactly(_tmp, "Tools/test-gate.sh"), Is.True);
            Assert.That(ArchitectureScanner.ExistsExactly(_tmp, "tools/test-gate.sh"), Is.False);
            Write("notes.txt", "x");
            Assert.That(ArchitectureScanner.ScanFiles(_tmp, new[] { "notes.txt" }, projectRules: false).Select(x => x.RuleId), Does.Contain("ARCH130"),
                "a file that really exists is still judged");
        }

        [Test]
        public void ProjectSettings_MustBeForceText()
        {
            Write("UnityProject/Assets/_Game/Core/Runtime/X.cs", "class X {}");
            Assert.That(ScanAll().Select(x => x.RuleId), Does.Contain("ARCH084"));
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "EditorSettings:\n  m_SerializationMode: 1\n");
            Assert.That(ScanAll().Select(x => x.RuleId), Does.Contain("ARCH084"));
        }

        private void Asmdef(string rel, string name, string refs, bool pure, bool autoRefFalse = true, bool overrideRefs = false)
        {
            Write(rel, "{ \"name\": \"" + name + "\", \"references\": [" + refs + "], " +
                       (autoRefFalse ? "\"autoReferenced\": false, " : "") +
                       "\"overrideReferences\": " + (overrideRefs ? "true" : "false") + ", " +
                       "\"noEngineReferences\": " + (pure ? "true" : "false") + " }");
        }

        [Test]
        public void Asmdefs_GraphRules()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Asmdef("UnityProject/Assets/_Game/Core/Runtime/Game.Core.asmdef", "Game.Core", "\"UniTask\"", true);
            Asmdef("UnityProject/Assets/_Game/Features/A/Logic/Game.A.asmdef", "Game.A", "\"Game.B\", \"GUID:0123\", \"Newtonsoft.Json\"", false);
            Asmdef("UnityProject/Assets/_Game/Features/A/Tests/Game.A.Tests.asmdef", "Game.A.Tests", "\"Game.A\"", true, overrideRefs: true);
            Asmdef("UnityProject/Assets/_Game/Features/B/Logic/Game.Wrong.asmdef", "Game.Wrong", "\"Game.A\"", true);
            Asmdef("UnityProject/Assets/_Game/Features/B/Logic/Game.B.asmdef", "Game.B", "\"Game.A\", \"Game.C.Unity\"", true, autoRefFalse: false);
            Asmdef("UnityProject/Assets/_Game/Features/C/Contracts/Game.C.Contracts.asmdef", "Game.C.Contracts", "\"Game.A\"", true);
            Asmdef("UnityProject/Assets/_Game/Features/C/Unity/Game.C.Unity.asmdef", "Game.C.Unity", "", false, overrideRefs: true);
            Asmdef("UnityProject/Assets/_Game/Bootstrap/Game.Bootstrap.asmdef", "Game.Bootstrap", "\"Game.A\"", false);
            Asmdef("UnityProject/Assets/_Game/Architecture/Rules/Game.Architecture.Rules.asmdef", "Game.Architecture.Rules", "", true); // no includePlatforms → could ship

            var ids = ScanAll().Select(x => x.RuleId).ToList();

            foreach (var id in new[] { "ARCH001", "ARCH002", "ARCH003", "ARCH004", "ARCH005", "ARCH006", "ARCH007", "ARCH008", "ARCH009", "ARCH010", "ARCH011", "ARCH012", "ARCH013" })
                Assert.That(ids, Does.Contain(id), id);
        }

        // ---------- UI testability & dev tools ----------

        private const string ViewFile = "UnityProject/Assets/_Game/Features/A/Unity/Views/AView/AView.cs";

        [Test]
        public void Layout_OnlyInTheRecipe_ViewsMoveOnlyFromData()
        {
            Fires("ARCH100", ViewFile, "class AView : MonoBehaviour { void F() { title.sizeDelta = new Vector2(48f, 0f); } }");
            Fires("ARCH100", ViewFile, "class AView : MonoBehaviour { void F(RectOffset p) { group.padding = p; } }");
            Fires("ARCH100", ViewFile, "class AView : MonoBehaviour { void F() { rt.SetSizeWithCurrentAnchors(Axis.Vertical, 402); } }");
            Fires("ARCH100", ViewFile, "class AView : MonoBehaviour { void F(float x) { rt.anchorMin = new Vector2(x, 0.15f); // layout: dynamic\n } }");
            Fires("ARCH100", "UnityProject/Assets/_Game/Features/A/Unity/AInstaller.cs", "class I { void F() { panel.anchoredPosition = Vector2.up; } }");
            Silent("ARCH100", ViewFile, "class AView : MonoBehaviour { void F(Vector2 min) { rt.anchorMin = min; // layout: dynamic\n } }");
            Silent("ARCH100", ViewFile, "class AView : MonoBehaviour { void F(float x) { var m = rt.anchorMax; m.x = Mathf.Clamp01(x); rt.anchorMax = m; // layout: dynamic\n } }");
            Silent("ARCH100", ViewFile, "class AView : MonoBehaviour { bool F() => rt.sizeDelta == Vector2.zero; }"); // comparison, not a write
            Silent("ARCH100", "UnityProject/Assets/_Game/Features/A/Unity/Views/AView/AViewLayout.cs", "static class AViewLayout { static void F() { rt.sizeDelta = new Vector2(48f, 0f); } }");
            Silent("ARCH100", "UnityProject/Assets/_Game/Features/A/Editor/Recipe.cs", "static class R { static void F() { rt.sizeDelta = new Vector2(48f, 0f); } }");
        }

        [Test]
        public void DevTools_MayGetInternals_AndArePure()
        {
            Silent("ARCH021", Logic, "[assembly: InternalsVisibleTo(\"Game.A.DevTools\")]");
            Fires("ARCH050", "UnityProject/Assets/_Game/Features/A/DevTools/Fixtures/AViewFixtures.cs", "using UnityEngine;\nclass F { }");
        }

        [Test]
        public void Views_HaveFolderFixturesAndCases()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Loose.cs", "class LooseView : MonoBehaviour, ILooseView { }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Views/GoodView/GoodView.cs", "class GoodView : MonoBehaviour, IGoodView { }");
            Write("UnityProject/Assets/_Game/Features/A/DevTools/Fixtures/GoodViewFixtures.cs", "class F { object S = new ViewFixture<IGoodView>(FixtureTags.Smoke, v => { }); }");
            Write("UnityProject/Assets/_Game/Features/A/Editor/Views/GoodViewCases.cs", "class C { }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Views/BareView/BareView.cs", "public sealed class BareView : MonoBehaviour, IBareView { }");
            Write("UnityProject/Assets/_Game/Features/A/DevTools/Fixtures/BareViewFixtures.cs", "class F { object S = new ViewFixture<IBareView>(\"first\", v => { }); }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Hud.cs", "class Hud : MonoBehaviour { }"); // not a view (no I*View)

            var v = ScanAll().Where(x => x.RuleId == "ARCH101").Select(x => x.ToString()).ToList();
            string all = string.Join("\n", v);

            Assert.That(v, Has.Some.Contains("LooseView must live in UnityProject/Assets/_Game/Features/A/Unity/Views/LooseView/LooseView.cs"), all);
            Assert.That(v, Has.Some.Contains("BareViewFixtures.cs no fixture named \"smoke\""), all);
            Assert.That(v, Has.Some.Contains("BareView has no view cases"), all);
            Assert.That(v.Any(x => x.Contains("GoodView")), Is.False, all);
            Assert.That(v.Any(x => x.Contains("Hud")), Is.False, all);
            Assert.That(UiRules.FindViews(_tmp).Select(x => x.Id), Is.EqualTo(new[] { "A/BareView", "A/GoodView", "A/LooseView" }));
        }

        [Test]
        public void DevTools_NeverReachARelease()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Write("UnityProject/Assets/_Game/Core/DevTools/Game.Core.DevTools.asmdef",
                "{ \"name\": \"Game.Core.DevTools\", \"references\": [], \"autoReferenced\": false, \"noEngineReferences\": true, \"defineConstraints\": [ \"UNITY_EDITOR || GAME_DEVTOOLS\" ] }");
            Write("UnityProject/Assets/_Game/Features/A/DevTools/Game.A.DevTools.asmdef",
                "{ \"name\": \"Game.A.DevTools\", \"references\": [\"Game.Core.DevTools\"], \"autoReferenced\": false, \"noEngineReferences\": true }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Game.A.Unity.asmdef",
                "{ \"name\": \"Game.A.Unity\", \"references\": [\"Game.A.DevTools\"], \"autoReferenced\": false }");
            Write("UnityProject/Assets/_Game/Features/A/Editor/Game.A.Editor.asmdef",
                "{ \"name\": \"Game.A.Editor\", \"references\": [\"Game.A.DevTools\"], \"includePlatforms\": [\"Editor\"], \"autoReferenced\": false }");

            var v = ScanAll().Where(x => x.RuleId == "ARCH102").Select(x => x.ToString()).ToList();
            string all = string.Join("\n", v);

            Assert.That(v, Has.Count.EqualTo(2), all);
            Assert.That(v, Has.Some.Contains("Game.A.DevTools: dev-tools assembly must set"), all);
            Assert.That(v, Has.Some.Contains("Game.A.Unity: runtime assembly references dev-tools assembly Game.A.DevTools"), all);
        }

        // ---------- product plan ----------

        [Test]
        public void Plan_ParsesStrictly_AndCoversMustNameRealRequirements()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Directory.CreateDirectory(Path.Combine(_tmp, "UnityProject/Assets/_Game/Features/A"));
            Write("Docs/Plan/features.md",
                "# Plan\n## F-A  Feature A  [P0] [modules: A]\n- R-A-1  Does a thing\ndeps: hard F-B\n" +
                "## F-B  Feature B  [P1] [modules: Nope]\n- R-B-1  Other\nthis line is a description\n- not a requirement\ndeps: hard F-A · maybe F-C\n" +
                "## F-C  C  [P9]\n");
            Write("UnityProject/Assets/_Game/Features/A/Tests/ATests.cs", "class ATests {\n [Test, Covers(\"R-A-1\")] public void Ok() {}\n [Test, Covers(\"R-TYPO\")] public void Bad() {}\n}");

            var v = ScanAll();
            string all = string.Join("\n", v);
            var p = v.Where(x => x.RuleId == "ARCH110").Select(x => x.Message).ToList();
            Assert.That(p, Has.Some.Contains("- R-ID  Text"), all);                 // malformed requirement line
            Assert.That(p, Has.Some.Contains("'hard F-X'"), all);                   // malformed dependency
            Assert.That(p, Has.Some.Contains("unknown feature tag [P9]"), all);
            Assert.That(p, Has.Some.Contains("module 'Nope'"), all);
            Assert.That(p, Has.Some.Contains("dependency cycle"), all);
            Assert.That(p, Has.Some.Contains("F-C: add [modules: X]"), all);
            var c = v.Where(x => x.RuleId == "ARCH111").ToList();
            Assert.That(c, Has.Count.EqualTo(1), all);
            Assert.That(c[0].Line, Is.EqualTo(3));
            Assert.That(PlanRules.Covers(_tmp).Select(x => x.TestKey + "=" + x.Requirement), Is.EquivalentTo(new[] { "ATests.Ok=R-A-1", "ATests.Bad=R-TYPO" }));

            // A nested helper class declared before the test must not be taken as the test's class.
            var nested = PlanRules.CoversIn("UnityProject/Assets/_Game/Features/A/Tests/B.cs",
                "class BTests {\n private sealed class FakeStore { public void Save() {} }\n [Test, Covers(\"R-A-1\")] public void Loads() {}\n [Covers(\"R-A-1\")] class Inner { }\n}").ToList();
            Assert.That(nested.Select(x => x.TestKey), Is.EqualTo(new[] { "BTests.Loads", "Inner.*" }));
        }

        // ---------- art slots ----------

        [Test]
        public void ArtSlots_DeclaredOnce_ByTheirFeature_AndBindingsNameRealSlots()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Write("UnityProject/Assets/_Game/Features/A/Logic/Domain/AArtSlots.cs",
                "class AArtSlots { const string HeroId = \"A.Hero\"; object H = new ArtSlotContract(HeroId, k); object B = new ArtSlotContract(\"B.Boss\", k); object D = new ArtSlotContract(\"A.Hero\", k); object X = new ArtSlotContract(Other.Id, k); }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Elsewhere.cs", "class E { object S = new ArtSlotContract(\"A.Side\", k); }");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/HeroArt.asset", "MonoBehaviour:\n  slotId: A.Hero\n  candidate: {fileID: 0}\n  final: {fileID: 100100000, guid: " + G('f') + ", type: 3}\n");
            Write("UnityProject/Assets/_Game/Features/A/Assets/Private/Typo.asset", "MonoBehaviour:\n  slotId: A.Heroo\n");

            var v = ScanAll().Where(x => x.RuleId == "ARCH120" || x.RuleId == "ARCH121").Select(x => x.ToString()).ToList();
            string all = string.Join("\n", v);
            Assert.That(v, Has.Some.Contains("'B.Boss' must start with its feature: 'A."), all);
            Assert.That(v, Has.Some.Contains("'A.Hero' declared 2 times"), all);
            Assert.That(v, Has.Some.Contains("same-file const (found Other)"), all);
            Assert.That(v, Has.Some.Contains("Elsewhere.cs").And.Contains("declare art slots in"), all);
            Assert.That(v, Has.Some.Contains("Typo.asset").And.Contains("'A.Heroo'"), all);
            Assert.That(v.Any(x => x.Contains("HeroArt.asset")), Is.False, all);
            Assert.That(ArtRules.Bindings(_tmp).Single(b => b.SlotId == "A.Hero").FinalGuid, Is.EqualTo(G('f')));
        }

        // ---------- files: layout, junk, broken GUID references ----------

        [TestCase("UnityProject/Assets/_Game/Features/A/Logic/X.cs", null)]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Private/Hero.prefab", null)]
        [TestCase("UnityProject/Assets/_Game/Features/A/README.md", null)]
        [TestCase("UnityProject/Assets/_Game/Scenes/Main.unity", null)]
        [TestCase(".scratch/Try.unity", null)]
        [TestCase(".claude/settings.json", null)]
        [TestCase("Docs/PLANNING.md", null)]
        [TestCase("UnityProject/Assets/Foo/X.cs", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Core/loose.cs", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Art/wolf.fbx", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Misc/x.cs", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Temp/x.png", "ARCH130")]
        [TestCase("notes.txt", "ARCH130")]
        // Root layout (hard rule): Root = UnityProject/ Tools/ Dashboard/ Docs/ Template/ + AGENTS.md, CLAUDE.md, README.md, dot-entries
        [TestCase("AGENTS.md", null)]
        [TestCase(".github/workflows/logic-tests.yml", null)]
        [TestCase("Tools/GitHooks/pre-commit", null)]
        [TestCase("Tools/LogicTests/AgentControl/X.cs", null)]
        [TestCase("Dashboard/index.html", null)]
        [TestCase("Docs/Plan/Inbox/idea.md", null)]
        [TestCase("Docs/Architecture/Features/A.md", null)]
        [TestCase("UnityProject/Packages/manifest.json", null)]
        [TestCase("UnityProject/ProjectSettings/ProjectSettings.asset", null)]
        [TestCase("Assets/_Game/Features/A/Logic/X.cs", "ARCH130")]           // the old layout: Unity lives in UnityProject/
        [TestCase("docs/RULES.md", "ARCH130")]
        [TestCase("Scripts/build.sh", "ARCH130")]
        [TestCase("Tools/git-hooks/pre-commit", "ARCH130")]                    // folders are PascalCase
        [TestCase("Docs/plan/features.md", "ARCH130")]
        [TestCase("UnityProject/Tools/x.sh", "ARCH130")]                      // only what Unity needs
        [TestCase("UnityProject/notes.txt", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Private/New Scene.unity", "ARCH131")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Private/Slime copy.prefab", "ARCH131")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Private/Slime (1).prefab", "ARCH131")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Logic/Arena.cs.orig", "ARCH131")]
        [TestCase("UnityProject/Assets/_Game/Features/A/Assets/Private/Test.unity", "ARCH131")]
        [TestCase("UnityProject/Assets/_Game/Core.meta", null)]
        [TestCase("UnityProject/Assets/_Game/Core/Runtime.meta", null)]
        [TestCase("UnityProject/Assets/_Game/Features.meta", null)]
        [TestCase("UnityProject/Assets/_Game/Core/Foo.meta", "ARCH130")]
        [TestCase("UnityProject/Assets/_Game/Core/loose.cs.meta", "ARCH130")]
        public void FilePlacement(string path, string rule)
        {
            var ids = FileRules.CheckPath(path).Select(v => v.RuleId).ToList();
            foreach (var id in ids)
                Fired.Add(id);
            if (rule == null)
                Assert.That(ids, Is.Empty, path);
            else
                Assert.That(ids, Does.Contain(rule), path);
        }

        private void GitIn(string args)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
                { WorkingDirectory = _tmp, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        [Test]
        public void MovedWithoutItsMeta_IsCaught_WithTheExactFix_AndADeletedAssetToo()
        {
            const string P = "UnityProject/Assets/_Game/Features/A/Assets/Private/";
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Asset(P + "Art/Wolf.fbx", G('a'), "FBX-wolf\n");
            Asset(P + "Boss.prefab", G('b'), "PrefabInstance:\n  m_SourcePrefab: {fileID: 100100000, guid: " + G('a') + ", type: 3}\n");
            GitIn("init -q");
            GitIn("add -A");
            GitIn("-c user.email=a@b -c user.name=t commit -qm init");
            Assert.That(ScanAll().Any(x => x.RuleId == "ARCH132"), Is.False);

            Directory.CreateDirectory(Path.Combine(_tmp, P + "Characters"));
            File.Move(Path.Combine(_tmp, P + "Art/Wolf.fbx"), Path.Combine(_tmp, P + "Characters/Wolf.fbx"));
            File.Delete(Path.Combine(_tmp, P + "Art/Wolf.fbx.meta"));
            Write(P + "Characters/Wolf.fbx.meta", "fileFormatVersion: 2\nguid: " + G('9') + "\n"); // Unity's new identity

            var v = ScanAll().Single(x => x.RuleId == "ARCH132");
            Assert.That(v.Path, Is.EqualTo(P + "Boss.prefab"));
            Assert.That(v.Line, Is.EqualTo(2));
            StringAssert.Contains("MOVED to " + P + "Characters/Wolf.fbx", v.Message);
            StringAssert.Contains("git show HEAD:" + P + "Art/Wolf.fbx.meta > \"" + P + "Characters/Wolf.fbx.meta\"", v.Message);

            File.WriteAllText(Path.Combine(_tmp, P + "Characters/Wolf.fbx.meta"), "fileFormatVersion: 2\nguid: " + G('a') + "\n"); // the fix
            Assert.That(ScanAll().Any(x => x.RuleId == "ARCH132"), Is.False);

            File.Delete(Path.Combine(_tmp, P + "Characters/Wolf.fbx"));
            File.Delete(Path.Combine(_tmp, P + "Characters/Wolf.fbx.meta"));
            StringAssert.Contains("It was deleted. Restore it", ScanAll().Single(x => x.RuleId == "ARCH132").Message);
        }

        // ---------- system map, docs, third-party, exceptions ----------

        private static readonly DateTime Today = new DateTime(2026, 6, 1);

        private List<RuleViolation> ScanAt(DateTime today)
        {
            var v = ArchitectureScanner.ScanAll(_tmp, today);
            foreach (var x in v)
                Fired.Add(x.RuleId);
            return v;
        }

        private void MinimalProject()
        {
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
            Write("UnityProject/Packages/manifest.json", "{ \"dependencies\": { \"com.x.tween\": \"2.0.0\" } }");
            Asmdef("UnityProject/Assets/_Game/Core/Runtime/Game.Core.asmdef", "Game.Core", "", true);
            Write("UnityProject/Assets/_Game/Core/Runtime/Foo.cs", "namespace Game.Core { public sealed class Foo { public void Baz() { } } }");
            Write("UnityProject/Assets/_Game/Features/A/README.md", "#");
            Asmdef("UnityProject/Assets/_Game/Features/A/Unity/Game.A.Unity.asmdef", "Game.A.Unity", "\"Game.Core\", \"Sdk1\"", false);
        }

        [Test]
        public void Overview_StaleIsReported_FreshIsNot()
        {
            MinimalProject();
            ArchitectureDocs.WriteAll(_tmp);
            Write(ArchitectureDocs.OverviewPath, "old diagram");
            Assert.That(ScanAt(Today).Select(x => x.RuleId), Does.Contain("ARCH090"));

            ArchitectureDocs.WriteAll(_tmp);
            Assert.That(ScanAt(Today).Select(x => x.RuleId), Does.Not.Contain("ARCH090"));
        }

        [Test]
        public void DocAnchors_OnlyOurNamesAndPaths()
        {
            MinimalProject();
            Write("Docs/guide.md",
                "Valid: `Assets/_Game/Core/Runtime/Foo.cs` `Foo.Baz()` `Game.Core.Foo` `Activator.CreateInstance` `Assets/_Game/Features/X/Logic`\n" +
                "Broken: `Assets/_Game/Core/Runtime/Gone.cs` `Foo.Removed` `Game.Nope.Contracts`\n" +
                "```\n`Assets/_Game/InsideFence.cs`\n```\n");

            var anchors = ScanAt(Today).Where(x => x.RuleId == "ARCH091").ToList();
            string all = string.Join("\n", anchors);
            Assert.That(anchors.Count, Is.EqualTo(3), all);
            Assert.That(all, Does.Contain("Gone.cs").And.Contain("Removed").And.Contain("Game.Nope.Contracts"));
        }

        [Test]
        public void ThirdParty_TiersAdaptersProvenance()
        {
            MinimalProject();
            Write("UnityProject/Assets/ThirdParty/Sdk1/Sdk1.cs", "class Sdk1 {}");
            Write("UnityProject/Assets/ThirdParty/Sdk1/Plugin.dll", "MZ");
            Write("UnityProject/Assets/ThirdParty/Sdk1/Plugin.dll.meta", "PluginImporter:\n  isExplicitlyReferenced: 0\n");
            Write("UnityProject/Assets/Plugins/Hidden.cs", "class Hidden {}");   // code smuggled outside Assets/_Game
            Write(ArchitecturePolicy.FileName,
                "vendor id=sdk1 tier=sdk assemblies=Sdk1 adapter=Game.A.Unity source=Assets/ThirdParty/Sdk1 sha256=deadbeef\n" +
                "vendor id=tween tier=presentation assemblies=Tween package=com.x.tween version=1.0.0\n");
            Asmdef("UnityProject/Assets/_Game/Features/B/Logic/Game.B.asmdef", "Game.B", "\"Game.Core\", \"Tween\", \"Sdk1\", \"Mystery\"", true);
            Write("UnityProject/Assets/_Game/Features/B/README.md", "#");
            Asmdef("UnityProject/Assets/_Game/Features/B/Tests/Game.B.Tests.asmdef", "Game.B.Tests", "\"Game.B\"", true, overrideRefs: true);

            var v = ScanAt(Today);
            string all = string.Join("\n", v);

            Assert.That(v.Any(x => x.RuleId == "ARCH092" && x.Path.EndsWith("Plugin.dll")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH093" && x.Message.Contains("files changed")), Is.True, all);            // hash
            Assert.That(v.Any(x => x.RuleId == "ARCH093" && x.Message.Contains("policy pins 1.0.0")), Is.True, all);       // version
            Assert.That(v.Any(x => x.RuleId == "ARCH093" && x.Path == "UnityProject/Assets/Plugins/Hidden.cs"), Is.True, all);         // smuggled
            Assert.That(v.Any(x => x.RuleId == "ARCH094" && x.Message.Contains("'Tween' is not allowed in pure code")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH094" && x.Message.Contains("Game.B uses SDK 'Sdk1' directly")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH094" && x.Message.Contains("unregistered third-party 'Mystery'")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH094" && x.Path.Contains("Game.A.Unity")), Is.False, "the adapter may use its SDK");
            Assert.That(v.Any(x => x.RuleId == "ARCH002" && x.Message.Contains("Tween")), Is.True, "presentation vendor is not foundation");
        }

        [Test]
        public void Exceptions_ScopedTimeBoxedAndNotForPolicyRules()
        {
            MinimalProject();
            Write("UnityProject/Assets/ThirdParty/Sdk1/Sdk1.cs", "class Sdk1 {}");
            string hash = ThirdPartyRules.HashFolder(Path.Combine(_tmp, "UnityProject/Assets/ThirdParty/Sdk1"));
            Write("UnityProject/Assets/_Game/Features/A/Unity/Finder.cs", "class Finder { void F() { GameObject.Find(\"HUD\"); } }");
            Write("UnityProject/Assets/_Game/Features/A/Unity/Old.cs", "class Old { void F() { Shader.Find(\"x\"); } }");
            Write(ArchitecturePolicy.FileName,
                "vendor id=sdk1 tier=sdk assemblies=Sdk1 adapter=Game.A.Unity source=Assets/ThirdParty/Sdk1 sha256=" + hash + "\n" +
                "exception rule=ARCH042 assembly=Game.A.Unity owner=platform expires=2099-01-01 reason=\"migrating HUD lookup\"\n" +
                "exception rule=ARCH093 assembly=Game.A.Unity owner=platform expires=2099-01-01 reason=\"nope\"\n" +
                "exception rule=ARCH041 assembly=Game.A.Unity owner=platform expires=2000-01-01 reason=\"old debt\"\n" +
                "exception rule=ARCH040 assembly=Game.A.Unity expires=2099-01-01\n");

            var v = ScanAt(Today);
            string all = string.Join("\n", v);

            Assert.That(v.Any(x => x.RuleId == "ARCH042"), Is.False, "valid exception waives ARCH042 in Game.A.Unity\n" + all);
            Assert.That(v.Any(x => x.RuleId == "ARCH095" && x.Message.Contains("policy rules cannot be excepted")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH095" && x.Message.Contains("EXPIRED")), Is.True, all);
            Assert.That(v.Any(x => x.RuleId == "ARCH095" && x.Message.Contains("owner= and reason")), Is.True, all);
        }

        [Test, Order(int.MaxValue)]
        public void Coverage_EveryStaticRuleHasAPositiveFixture()
        {
            // Runs last (Order) after the fixtures above have recorded which rules fired.
            var missing = RuleCatalog.All.Where(r => r.IsStatic && !Fired.Contains(r.Id)).Select(r => r.Id).ToList();
            Assume.That(Fired.Count, Is.GreaterThan(10), "run the whole fixture, not a single test");
            Assert.That(missing, Is.Empty, "Add a positive fixture for each static rule");
        }
    }
}
