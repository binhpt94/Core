using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Core.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// Builds what the game binds FROM the art version store, so nobody wires art by hand:
    ///   Features/F/Assets/Private/Art/&lt;Name&gt;/vNNN.png (+ .svg)   immutable versions (`art add`, `art codex`)
    ///   .../&lt;Name&gt;/versions.txt                                  history + "use candidate vNNN" / "use final vNNN" (`art use`)
    ///   .../&lt;Name&gt;/&lt;Name&gt;.candidate.prefab, .final.prefab      GENERATED: RectTransform + Image of the version in use,
    ///                                                           size = PNG / DeliveryScale, pivot = the contract's
    ///   .../&lt;Name&gt;/&lt;Name&gt;.art.asset                              GENERATED binding (ArtSlotAsset): what features reference
    /// Runs after a version PNG or versions.txt is imported (Tools > Game > Art > Sync to force). Idempotent: unchanged inputs
    /// write nothing. Size is not forced into the envelope: wrong art is reported by the art gate (A-SIZE), not hidden.
    /// </summary>
    public sealed class ArtImport : AssetPostprocessor
    {
        /// <summary>Art is delivered at this multiple of contract units. Keep in sync with TestGate's ArtBrief.DeliveryScale.</summary>
        public const int DeliveryScale = 2;
        public const string VersionLog = "versions.txt";
        public static string[] Tiers => new[] { "candidate", "final" };
        private static readonly Regex VersionPng = new Regex(@"/Art/[^/]+/v\d{3}\.png$");
        [AllowStatic("Editor-only debounce: many assets import in one batch, Sync runs once per batch (cleared in its delayCall).")]
        private static bool _pending;

        /// <summary>Unity folder of a slot's versions: Assets/_Game/Features/F/Assets/Private/Art/&lt;Name&gt;/.</summary>
        public static string SlotDir(string slotId)
        {
            int dot = slotId.IndexOf('.');
            return "Assets/_Game/Features/" + slotId.Substring(0, dot) + "/Assets/Private/Art/" + slotId.Substring(dot + 1) + "/";
        }

        private static string Name(string slotId) => slotId.Substring(slotId.IndexOf('.') + 1);
        public static string BindingPath(string dir, string slotId) => dir + Name(slotId) + ".art.asset";
        public static string PrefabPath(string dir, string slotId, string tier) => dir + Name(slotId) + "." + tier + ".prefab";

        private void OnPreprocessTexture()
        {
            if (VersionPng.IsMatch(assetPath))
                ConfigureSprite((TextureImporter)assetImporter);
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            bool relevant = imported.Concat(deleted).Concat(moved).Any(p => VersionPng.IsMatch(p) || (p.Contains("/Art/") && p.EndsWith("/" + VersionLog, StringComparison.Ordinal)));
            if (!relevant || _pending)
                return;
            _pending = true;
            EditorApplication.delayCall += () =>
            {
                _pending = false;
                var changes = Sync();
                if (changes.Count > 0)
                    Debug.Log("[ArtImport] " + string.Join("; ", changes));
            };
        }

        [MenuItem("Tools/Game/Art/Sync")]
        private static void Menu()
        {
            var changes = Sync();
            Debug.Log("[ArtImport] " + (changes.Count == 0 ? "up to date" : string.Join("; ", changes)));
        }

        /// <summary>Every declared slot that has a version store.</summary>
        public static List<string> Sync() => Sync(ArtProbe.Contracts(), SlotDir);

        public static List<string> Sync(IEnumerable<ArtSlotContract> contracts, Func<string, string> slotDir)
        {
            var changes = new List<string>();
            foreach (var c in contracts.Where(c => c.Kind == ArtSlotKind.UiImage))
            {
                string dir = slotDir(c.Id);
                var use = ReadUse(dir + VersionLog);
                if (use == null && AssetDatabase.LoadAssetAtPath<ArtSlotAsset>(BindingPath(dir, c.Id)) == null)
                    continue; // no versions, no binding: the graybox stays
                var bound = new Dictionary<string, GameObject>();
                foreach (var tier in Tiers)
                {
                    string png = use != null && use.TryGetValue(tier, out var id) ? dir + id + ".png" : null;
                    bound[tier] = png != null && File.Exists(png) ? EnsurePrefab(c, PrefabPath(dir, c.Id, tier), png, changes) : RemovePrefab(PrefabPath(dir, c.Id, tier), changes);
                }
                EnsureBinding(c.Id, dir, bound["candidate"], bound["final"], changes);
            }
            if (changes.Count > 0)
                AssetDatabase.SaveAssets();
            return changes;
        }

        /// <summary>The slot's binding asset, created empty (graybox) if missing: features reference it once, versions change behind it.</summary>
        public static ArtSlotAsset EnsureBinding(string slotId) =>
            EnsureBinding(slotId, SlotDir(slotId), null, null, new List<string>(), keepRefs: true);

        private static ArtSlotAsset EnsureBinding(string slotId, string dir, GameObject candidate, GameObject final, List<string> changes, bool keepRefs = false)
        {
            string path = BindingPath(dir, slotId);
            var asset = AssetDatabase.LoadAssetAtPath<ArtSlotAsset>(path);
            if (asset == null)
            {
                EnsureFolder(dir);
                asset = ScriptableObject.CreateInstance<ArtSlotAsset>();
                AssetDatabase.CreateAsset(asset, path);
                changes.Add("created " + path);
            }
            var so = new SerializedObject(asset);
            bool dirty = Set(so.FindProperty("slotId"), slotId);
            if (!keepRefs)
            {
                dirty |= Set(so.FindProperty("candidate"), candidate);
                dirty |= Set(so.FindProperty("final"), final);
            }
            if (dirty)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                changes.Add(slotId + " binding: candidate=" + (candidate != null ? candidate.name : "-") + " final=" + (final != null ? final.name : "-"));
            }
            return asset;
        }

        private static GameObject EnsurePrefab(ArtSlotContract c, string path, string png, List<string> changes)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(png);
            if (importer != null && ConfigureSprite(importer))
                importer.SaveAndReimport();
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(png);
            if (sprite == null)
            {
                changes.Add(png + " is not a sprite yet (import pending)");
                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            var size = new Vector2(Mathf.Round(sprite.rect.width / DeliveryScale), Mathf.Round(sprite.rect.height / DeliveryScale));
            var pivot = new Vector2(c.PivotXPercent / 100f, c.PivotYPercent / 100f);

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && existing.transform is RectTransform ert && existing.GetComponent<Image>() is Image ei &&
                ei.sprite == sprite && ert.sizeDelta == size && ert.pivot == pivot)
                return existing;

            var go = new GameObject("Art " + c.Id, typeof(RectTransform), typeof(Image));
            try
            {
                var rt = (RectTransform)go.transform;
                rt.pivot = pivot;     // layout: dynamic (from the slot contract)
                rt.sizeDelta = size;  // layout: dynamic (from the art version)
                var image = go.GetComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
                var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
                changes.Add(path + " ← " + Path.GetFileName(png) + " (" + size.x + "x" + size.y + ")");
                return saved;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static GameObject RemovePrefab(string path, List<string> changes)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && AssetDatabase.DeleteAsset(path))
                changes.Add("removed " + path);
            return null;
        }

        /// <summary>Sprite, single, no mipmaps, alpha as transparency. True if anything changed.</summary>
        private static bool ConfigureSprite(TextureImporter ti)
        {
            bool changed = ti.textureType != TextureImporterType.Sprite || ti.spriteImportMode != SpriteImportMode.Single || ti.mipmapEnabled || !ti.alphaIsTransparency;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            return changed;
        }

        /// <summary>"use &lt;tier&gt; vNNN" lines of versions.txt (same format TestGate writes); null when the file is missing.</summary>
        public static Dictionary<string, string> ReadUse(string path)
        {
            if (!File.Exists(path))
                return null;
            var use = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 3 && f[0] == "use")
                    use[f[1]] = f[2];
            }
            return use;
        }

        /// <summary>Creates the Unity folder chain (AssetDatabase must know a folder before assets are created in it).</summary>
        public static void EnsureFolder(string dir)
        {
            string current = "";
            foreach (var part in dir.TrimEnd('/').Split('/'))
            {
                string next = current.Length == 0 ? part : current + "/" + part;
                if (current.Length > 0 && !AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        private static bool Set(SerializedProperty p, string value)
        {
            if (p.stringValue == value)
                return false;
            p.stringValue = value;
            return true;
        }

        private static bool Set(SerializedProperty p, UnityEngine.Object value)
        {
            // Compare ids, not ==: a reference to a just-deleted prefab is "== null" in Unity but still serialized (missing).
            if (p.objectReferenceInstanceIDValue == (value != null ? value.GetInstanceID() : 0))
                return false;
            p.objectReferenceValue = value;
            return true;
        }
    }
}
