using System.IO;
using Game.Core.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Core.EditorTools.Tests
{
    /// <summary>
    /// The version store drives the binding: "use" lines → generated prefabs (size = PNG / DeliveryScale, pivot = contract)
    /// → ArtSlotAsset. Runs on a fake slot in a temporary folder outside Assets/_Game (not validated, deleted afterwards).
    /// </summary>
    [Category("ArtImport")]
    public sealed class ArtImportTests
    {
        private const string Root = "Assets/ArtImportTestTmp";
        private const string Dir = Root + "/Hero/";
        private static readonly ArtSlotContract Hero = new ArtSlotContract("Test.Hero", ArtSlotKind.UiImage, 32, 72, 60, 110, 50, 0, 10, 20, null);

        [SetUp]
        public void SetUp()
        {
            ArtImport.EnsureFolder(Dir);
            Png("v001.png", 100, 170, Color.green);
            Png("v002.png", 80, 120, Color.red);
            Use("use candidate v001\n");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Root);

        private static void Png(string file, int w, int h, Color color)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            tex.SetPixels(pixels);
            File.WriteAllBytes(Dir + file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(Dir + file, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void Use(string lines) => File.WriteAllText(Dir + ArtImport.VersionLog, "v001 d candidate claude - \"\"\nv002 d candidate claude - \"\"\n" + lines);

        private static System.Collections.Generic.List<string> Sync() => ArtImport.Sync(new[] { Hero }, id => Dir);

        private static ArtSlotAsset Binding() => AssetDatabase.LoadAssetAtPath<ArtSlotAsset>(ArtImport.BindingPath(Dir, Hero.Id));

        [Test]
        public void UsedVersion_BecomesAPrefab_SizedFromThePng_PivotFromTheContract_AndIsBound()
        {
            Assert.That(Sync(), Is.Not.Empty);
            var b = Binding();
            Assert.That(b, Is.Not.Null);
            Assert.That(b.SlotId, Is.EqualTo(Hero.Id));
            var candidate = b.Binding(ArtLevel.Candidate);
            Assert.That(candidate, Is.Not.Null);
            Assert.That(b.Binding(ArtLevel.Final), Is.Null);
            var rt = (RectTransform)candidate.transform;
            Assert.That(rt.sizeDelta, Is.EqualTo(new Vector2(50, 85)), "PNG 100x170 at delivery scale 2");
            Assert.That(rt.pivot, Is.EqualTo(new Vector2(0.5f, 0f)), "contract pivot 50/0");
            Assert.That(candidate.GetComponent<Image>().sprite, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Sprite>(Dir + "v001.png")));
        }

        [Test]
        public void SecondSync_ChangesNothing()
        {
            Sync();
            Assert.That(Sync(), Is.Empty);
        }

        [Test]
        public void SwitchingVersions_RebindsBehindTheSameAsset_AndAnUnusedTierIsRemoved()
        {
            Sync();
            var asset = Binding();
            Use("use candidate v002\nuse final v001\n");
            Sync();
            Assert.That(Binding(), Is.SameAs(asset), "features keep referencing the same binding asset");
            Assert.That(((RectTransform)asset.Binding(ArtLevel.Candidate).transform).sizeDelta, Is.EqualTo(new Vector2(40, 60)));
            Assert.That(asset.Binding(ArtLevel.Final), Is.Not.Null);

            Use("use candidate v002\n");
            Sync();
            Assert.That(asset.Binding(ArtLevel.Final), Is.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(ArtImport.PrefabPath(Dir, Hero.Id, "final")), Is.Null);
        }

        [Test]
        public void SlotWithoutVersions_GetsNoBinding_UntilAFeatureAsksForOne()
        {
            Assert.That(ArtImport.Sync(new[] { Hero }, id => Root + "/Nobody/"), Is.Empty);
            Assert.That(AssetDatabase.LoadAssetAtPath<ArtSlotAsset>(ArtImport.BindingPath(Root + "/Nobody/", Hero.Id)), Is.Null);
        }
    }
}
