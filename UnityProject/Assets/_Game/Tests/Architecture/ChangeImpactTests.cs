using System;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>The test gate's file → gates table. "I moved it 2px" must run one view's layout gate and nothing else.</summary>
    public sealed class ChangeImpactTests
    {
        private string _tmp;
        private ChangeImpact _impact;

        private const string F = "UnityProject/Assets/_Game/Features/Shop/";
        private const string ScriptGuid = "11111111111111111111111111111111";
        private const string PrefabGuid = "22222222222222222222222222222222";

        private void Write(string rel, string content)
        {
            string full = Path.Combine(_tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "impact-" + Guid.NewGuid().ToString("N"));
            Write(F + "Unity/Views/ShopPopup/ShopPopup.cs", "public sealed class ShopPopup : MonoBehaviour, IShopPopupView { }");
            Write(F + "Unity/Views/ShopPopup/ShopPopup.cs.meta", "fileFormatVersion: 2\nguid: " + ScriptGuid + "\n");
            Write(F + "Unity/Views/ShopHud/ShopHud.cs", "public sealed class ShopHud : MonoBehaviour, IShopHudView { }");
            Write(F + "Assets/Private/ShopPopup.prefab", "--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: " + ScriptGuid + ", type: 3}\n");
            Write(F + "Assets/Private/ShopPopup.prefab.meta", "fileFormatVersion: 2\nguid: " + PrefabGuid + "\n");
            Write(F + "Assets/Private/ShopPopup_Winter.prefab", "PrefabInstance:\n  m_SourcePrefab: {fileID: 100100000, guid: " + PrefabGuid + ", type: 3}\n");
            Write(F + "Assets/Private/Coin.prefab", "--- !u!1 &1\nGameObject:\n  m_Name: Coin\n");
            Write(F + "Tests/Unity/Game.Shop.Unity.Tests.asmdef", "{ \"name\": \"Game.Shop.Unity.Tests\" }");
            _impact = new ChangeImpact(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tmp))
                Directory.Delete(_tmp, true);
        }

        private FileImpact C(string path) => _impact.Classify(path);

        [TestCase(F + "Unity/Views/ShopPopup/ShopPopupLayout.cs")] // the 2px case
        [TestCase(F + "Unity/Views/ShopPopup/ShopPopup.cs")]
        [TestCase(F + "Editor/Views/ShopPopupCases.cs")]
        [TestCase(F + "Tests/Layout/ShopPopup.layout.txt")]
        [TestCase(F + "Assets/Private/ShopPopup.prefab")]           // resolved by the view script's GUID
        [TestCase(F + "Assets/Private/ShopPopup_Winter.prefab")]    // Variant → base prefab → view
        public void OneView_OnlyItsLayoutGate(string path)
        {
            var i = C(path);
            Assert.That(i.Roles, Is.EqualTo(new[] { ChangeRole.View }), i.ToString());
            Assert.That(i.Views, Is.EqualTo(new[] { "Shop/ShopPopup" }));
        }

        [Test]
        public void Fixtures_RunPureTests_AndThatViewsGate()
        {
            var i = C(F + "DevTools/Fixtures/ShopPopupFixtures.cs");
            Assert.That(i.Roles, Is.EquivalentTo(new[] { ChangeRole.Pure, ChangeRole.View }));
            Assert.That(i.Views, Is.EqualTo(new[] { "Shop/ShopPopup" }));
        }

        [TestCase(F + "Logic/UseCases/ShopPresenter.cs")]
        [TestCase(F + "Logic/UseCases/ShopTexts.cs")]
        [TestCase(F + "DevTools/Scenarios/ShopScenarios.cs")]
        public void WhatViewsShow_RunsPureTests_AndEveryViewOfTheFeature(string path)
        {
            var i = C(path);
            Assert.That(i.Roles, Is.EquivalentTo(new[] { ChangeRole.Pure, ChangeRole.FeatureViews }));
            Assert.That(i.Views, Is.EquivalentTo(new[] { "Shop/ShopPopup", "Shop/ShopHud" }));
        }

        [TestCase(F + "Logic/Simulation/Economy.cs")]
        [TestCase(F + "Logic/Domain/Price.cs")]
        [TestCase(F + "Contracts/IShopService.cs")]
        [TestCase("UnityProject/Assets/_Game/Core/Runtime/Result.cs")]
        [TestCase(F + "Tests/PriceTests.cs")]
        [TestCase("UnityProject/Assets/_Game/Architecture/Rules/SourceRules.cs")]
        public void PureCode_OnlyDotnet(string path) =>
            Assert.That(C(path).Roles, Is.EqualTo(new[] { ChangeRole.Pure }), C(path).ToString());

        [TestCase("UnityProject/Assets/_Game/Bootstrap/GameCompositionRoot.cs")]
        [TestCase("UnityProject/Assets/_Game/Scenes/Main.unity")]
        [TestCase(F + "Tests/Unity/ShopFlowTests.cs")]
        public void Composition_RunsIntegration(string path) =>
            Assert.That(C(path).Roles, Does.Contain(ChangeRole.Integration), C(path).ToString());

        [Test]
        public void FeatureWiring_And_UnresolvedPrefabs_RunIntegrationAndTheFeaturesViews()
        {
            foreach (var path in new[] { F + "Unity/ShopInstaller.cs", F + "Assets/Private/Coin.prefab", F + "Assets/Public/ShopConfig.asset" })
            {
                var i = C(path);
                Assert.That(i.Roles, Is.EquivalentTo(new[] { ChangeRole.Integration, ChangeRole.FeatureViews }), i.ToString());
                Assert.That(i.Views, Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void SharedViewTooling_RunsEveryView()
        {
            var i = C("UnityProject/Assets/_Game/Core/Editor/ViewStage.cs");
            Assert.That(i.Roles, Is.EqualTo(new[] { ChangeRole.AllViews }));
            Assert.That(i.Views, Has.Count.EqualTo(2));
        }

        [Test]
        public void Docs_RunNothing_UnknownPaths_FailClosed()
        {
            Assert.That(C(F + "README.md").Roles, Is.EqualTo(new[] { ChangeRole.Docs }));
            Assert.That(C("UnityProject/Assets/Misc/data.bytes").Roles, Is.EqualTo(new[] { ChangeRole.Unknown }));
            Assert.That(C(F + "Unity/Views/ShopPopup/ShopPopup.cs.meta").Views, Is.EqualTo(new[] { "Shop/ShopPopup" }), ".meta follows its asset");
        }
    }
}
