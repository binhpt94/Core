using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core.Presentation;
using UnityEditor;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>One thing the art gate checks: a slot at one level (the generated graybox, or the bound candidate/final).</summary>
    public sealed class ArtCase
    {
        public ArtSlotContract Contract { get; set; }
        public ArtSlotAsset Binding { get; set; }
        public ArtLevel Level { get; set; }
        public string Name => Contract.Id + " [" + Level.ToString().ToLowerInvariant() + "]";
    }

    /// <summary>
    /// Finds every slot contract (features' IArtSlotCatalog, via TypeCache: Editor only) and every binding asset, and MEASURES
    /// bound art with Unity's own data (rect/sprite/renderer bounds, pivot, clips and their events). Judging is pure (ArtChecks).
    /// </summary>
    public static class ArtProbe
    {
        public static IReadOnlyList<ArtSlotContract> Contracts() =>
            TypeCache.GetTypesDerivedFrom<IArtSlotCatalog>()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .SelectMany(t => ((IArtSlotCatalog)Activator.CreateInstance(t, true)).Slots)
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();

        public static Dictionary<string, ArtSlotAsset> Bindings()
        {
            var map = new Dictionary<string, ArtSlotAsset>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(ArtSlotAsset)))
            {
                var a = AssetDatabase.LoadAssetAtPath<ArtSlotAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (a != null && !string.IsNullOrEmpty(a.SlotId))
                    map[a.SlotId] = a;
            }
            return map;
        }

        public static IEnumerable<ArtCase> Cases()
        {
            var bindings = Bindings();
            foreach (var c in Contracts())
            {
                bindings.TryGetValue(c.Id, out var b);
                yield return new ArtCase { Contract = c, Binding = b, Level = ArtLevel.Graybox };
                foreach (var level in new[] { ArtLevel.Candidate, ArtLevel.Final })
                    if (b != null && b.Binding(level) != null)
                        yield return new ArtCase { Contract = c, Binding = b, Level = level };
            }
        }

        public static ArtMeasurement Measure(ArtCase art)
        {
            if (art.Level == ArtLevel.Graybox)
            {
                if (art.Contract.Kind != ArtSlotKind.UiImage)
                    return ArtChecks.Graybox(art.Contract);
                var host = new GameObject("~ArtStage", typeof(RectTransform), typeof(Canvas)) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    return MeasureObject(art.Contract, Graybox.CreateUi(art.Contract, host.transform).gameObject);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
            }
            var prefab = art.Binding.Binding(art.Level);
            var stage = new GameObject("~ArtStage", typeof(RectTransform), typeof(Canvas)) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, stage.transform);
                return MeasureObject(art.Contract, instance);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(stage);
            }
        }

        private static ArtMeasurement MeasureObject(ArtSlotContract c, GameObject go)
        {
            var m = new ArtMeasurement();
            if (c.Kind == ArtSlotKind.UiImage && go.transform is RectTransform rt)
            {
                m.Width = Mathf.RoundToInt(rt.rect.width);
                m.Height = Mathf.RoundToInt(rt.rect.height);
                m.PivotXPercent = Mathf.RoundToInt(rt.pivot.x * 100);
                m.PivotYPercent = Mathf.RoundToInt(rt.pivot.y * 100);
            }
            else if (c.Kind == ArtSlotKind.Sprite2D && go.GetComponentInChildren<SpriteRenderer>() is SpriteRenderer sr && sr.sprite != null)
            {
                var size = sr.bounds.size;
                m.Width = Mathf.RoundToInt(size.x * 100);
                m.Height = Mathf.RoundToInt(size.y * 100);
                m.PivotXPercent = Mathf.RoundToInt(sr.sprite.pivot.x / sr.sprite.rect.width * 100);
                m.PivotYPercent = Mathf.RoundToInt(sr.sprite.pivot.y / sr.sprite.rect.height * 100);
            }
            else
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var b = renderers[0].bounds;
                    foreach (var r in renderers.Skip(1))
                        b.Encapsulate(r.bounds);
                    m.Width = Mathf.RoundToInt(b.size.x * 100);
                    m.Height = Mathf.RoundToInt(b.size.y * 100);
                    var origin = go.transform.position;
                    m.PivotXPercent = b.size.x > 0 ? Mathf.RoundToInt((origin.x - b.min.x) / b.size.x * 100) : 50;
                    m.PivotYPercent = b.size.y > 0 ? Mathf.RoundToInt((origin.y - b.min.y) / b.size.y * 100) : 0;
                }
            }

            var clips = new List<AnimationClip>();
            foreach (var animator in go.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null)
                    clips.AddRange(animator.runtimeAnimatorController.animationClips);
            foreach (var clip in clips.Where(x => x != null).GroupBy(x => x.name).Select(g => g.First()))
            {
                var ac = new ArtClip { LengthMs = Mathf.RoundToInt(clip.length * 1000) };
                foreach (var e in AnimationUtility.GetAnimationEvents(clip))
                {
                    // Marker name: the event's string parameter if set, else its function name (e.g. "land").
                    string marker = string.IsNullOrEmpty(e.stringParameter) ? e.functionName : e.stringParameter;
                    if (!string.IsNullOrEmpty(marker) && !ac.MarkersMs.ContainsKey(marker))
                        ac.MarkersMs[marker] = Mathf.RoundToInt(e.time * 1000);
                }
                m.Clips[clip.name] = ac;
            }
            return m;
        }

        /// <summary>Writes the measured line for the dashboard / review (not a blocking baseline: approval is `art accept`).</summary>
        public static void Record(ArtCase art, ArtMeasurement m, IReadOnlyList<string> violations)
        {
            string dir = Path.Combine(Application.dataPath, "..", "..", "Dashboard", "Data", "art");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, art.Contract.Id + "." + art.Level.ToString().ToLowerInvariant() + ".txt"),
                ArtChecks.Describe(art.Contract.Id, art.Level.ToString().ToLowerInvariant(), m) + "\n" + string.Join("\n", violations) + "\n");
        }

        public static string Category(string slotId) => "Art:" + slotId;
    }
}
