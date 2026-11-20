using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core.Presentation;
using UnityEditor;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// Pictures for judging art BY EYE (looks are not measured): every UiImage slot alone at each level it has, with its pivot
    /// at the image centre (graybox / candidate / final side by side), and every slot IN THE GAME where a view declares it
    /// (IViewCases.ArtPlacements: fixture + how to find it), on every device, with a sidecar .txt of where the slot sits. PNGs go to Dashboard/Data/art/shots/ (ignored, rewritten each run); `art brief` embeds them and
    /// `art compare` draws the contract and the slot's place over them. No scene, no Play Mode.
    ///   Tools > Game > Art > Snapshot (Editor open), or `./Tools/test-gate.sh art shots` (Editor closed: batchmode WITH a GPU).
    /// </summary>
    public static class ArtSnapshot
    {
        /// <summary>Isolates the stage from anything else the camera could see.</summary>
        private const int StageLayer = 31;
        private const int SlotPixelsPerUnit = 2;
        /// <summary>Slot picture = max envelope × 5/2 with the pivot at the centre: room for the art at ANY pivot (0..100 %).
        /// Keep in sync with TestGate's ArtCompare, which draws the contract over this picture.</summary>
        public const int MarginNumerator = 5, MarginDenominator = 2;
        /// <summary>Full canvas resolution: `art compare` crops a close-up of the slot out of this picture.</summary>
        private const float ViewScale = 1f;
        private static readonly Color ViewBackground = new Color(0.12f, 0.12f, 0.14f, 1f);

        public static string Dir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Dashboard", "Data", "art", "shots"));

        /// <summary>Slot shot: "&lt;slot&gt;.&lt;level&gt;.png". The image is the max envelope × 5/2, pivot at the centre.</summary>
        public static string SlotFile(string slotId, ArtLevel level) => slotId + "." + level.ToString().ToLowerInvariant() + ".png";

        /// <summary>In-game shot: "&lt;slot&gt;.inview.&lt;device&gt;.png" + ".txt": "view V fixture F canvas W H", then
        /// "rect x y w h" per instance (canvas units, origin top-left, y down).</summary>
        public static string InViewFile(string slotId, string device) => slotId + ".inview." + device + ".png";

        [MenuItem("Tools/Game/Art/Snapshot")]
        private static void Menu()
        {
            var files = CaptureAll();
            Debug.Log("Art snapshot: " + files.Count + " images in " + Dir + ". Regenerate briefs: ./Tools/test-gate.sh art brief --all");
        }

        /// <summary>
        /// Batchmode entry for `art shots` (Editor closed): -executeMethod Game.Core.EditorTools.ArtSnapshot.CaptureFromCommandLine.
        /// Needs a graphics device (never -nographics). Exits 0 = pictures written, 1 = no GPU, a blank picture or an exception.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            int code = 0;
            try
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Debug.LogError("[ArtSnapshot] no graphics device: run without -nographics, or use Tools > Game > Art > Snapshot in the Editor.");
                    code = 1;
                }
                else
                {
                    var files = CaptureAll();
                    Debug.Log("[ArtSnapshot] " + files.Count + " pictures written.");
                    foreach (var f in files.Where(IsBlank))
                    {
                        Debug.LogError("[ArtSnapshot] blank picture (the stage or camera is wrong, not the art): " + Path.GetFileName(f));
                        code = 1;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            EditorApplication.Exit(code);
        }

        /// <summary>One colour only: nothing was rendered.</summary>
        public static bool IsBlank(string png)
        {
            var tex = new Texture2D(2, 2);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(png)))
                    return true;
                var pixels = tex.GetPixels32();
                var first = pixels[0];
                return pixels.All(p => p.r == first.r && p.g == first.g && p.b == first.b && p.a == first.a);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        public static List<string> CaptureAll()
        {
            if (Directory.Exists(Dir))
                foreach (var old in Directory.GetFiles(Dir))
                    File.Delete(old); // generated, ignored: a removed slot or level must not leave a stale picture behind
            var files = CaptureSlots();
            files.AddRange(CaptureInView());
            return files;
        }

        public static List<string> CaptureSlots()
        {
            var files = new List<string>();
            foreach (var art in ArtProbe.Cases().Where(a => a.Contract.Kind == ArtSlotKind.UiImage))
            {
                var c = art.Contract;
                int w = c.MaxWidth * MarginNumerator / MarginDenominator, h = c.MaxHeight * MarginNumerator / MarginDenominator;
                var host = new GameObject("~ArtShot", typeof(RectTransform), typeof(Canvas)) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    host.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                    var canvas = (RectTransform)host.transform;
                    canvas.pivot = new Vector2(0.5f, 0.5f);
                    canvas.sizeDelta = new Vector2(w, h);
                    var go = art.Level == ArtLevel.Graybox
                        ? Graybox.CreateUi(c, host.transform).gameObject
                        : (GameObject)PrefabUtility.InstantiatePrefab(art.Binding.Binding(art.Level), host.transform);
                    if (go.transform is RectTransform rt)
                    {
                        // The art's own pivot lands on the image centre, so every level lines up in `art compare`.
                        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.anchoredPosition = Vector2.zero;
                    }
                    files.Add(Write(Render(canvas, w * SlotPixelsPerUnit, h * SlotPixelsPerUnit, Color.clear), SlotFile(c.Id, art.Level)));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
            }
            return files;
        }

        public static List<string> CaptureInView()
        {
            var files = new List<string>();
            var done = new HashSet<string>(StringComparer.Ordinal); // a slot shown by several views: the first view (by id) pictures it
            foreach (var cases in ViewCasesRegistry.All())
                foreach (var p in cases.ArtPlacements.Where(p => done.Add(p.SlotId)))
                    foreach (var device in cases.Devices)
                        using (var stage = ViewStage.Open(cases, p.Fixture, device))
                        {
                            var canvas = (RectTransform)stage.Root.GetComponentInChildren<Canvas>(true).transform;
                            var rects = p.Find(stage.Root).Select(rt => RectIn(canvas, rt)).ToList();
                            int w = Mathf.RoundToInt(canvas.rect.width * ViewScale), h = Mathf.RoundToInt(canvas.rect.height * ViewScale);
                            string path = Write(Render(canvas, w, h, ViewBackground), InViewFile(p.SlotId, device.Name));
                            File.WriteAllText(Path.ChangeExtension(path, ".txt"),
                                "view " + cases.ViewId + " fixture " + p.Fixture + " canvas " + Mathf.RoundToInt(canvas.rect.width) + " " +
                                Mathf.RoundToInt(canvas.rect.height) + "\n" + string.Concat(rects.Select(r => "rect " + r.x + " " + r.y + " " + r.w + " " + r.h + "\n")));
                            files.Add(path);
                        }
            return files;
        }

        /// <summary>Canvas units, origin top-left, y down (same convention as the layout snapshot).</summary>
        private static (int x, int y, int w, int h) RectIn(RectTransform canvas, RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 min = canvas.InverseTransformPoint(corners[0]), max = canvas.InverseTransformPoint(corners[2]);
            var c = canvas.rect;
            return (Mathf.RoundToInt(min.x - c.xMin), Mathf.RoundToInt(c.yMax - max.y), Mathf.RoundToInt(max.x - min.x), Mathf.RoundToInt(max.y - min.y));
        }

        /// <summary>Renders a world-space canvas, filling the image, with an orthographic camera that sees only the stage.</summary>
        public static Texture2D Render(RectTransform canvas, int width, int height, Color background)
        {
            foreach (var t in canvas.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = StageLayer;
            var cameraGo = new GameObject("~ArtShotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                var cam = cameraGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = canvas.rect.height * canvas.lossyScale.y / 2f;
                cam.aspect = (float)width / height;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = background;
                cam.cullingMask = 1 << StageLayer;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1000f;
                cam.transform.SetPositionAndRotation(canvas.TransformPoint(canvas.rect.center) + Vector3.back * 100f, Quaternion.identity);
                canvas.GetComponent<Canvas>().worldCamera = cam;
                Canvas.ForceUpdateCanvases();

                cam.targetTexture = target;
                cam.Render();
                RenderTexture.active = target;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                cam.targetTexture = null;
                return tex;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        private static string Write(Texture2D tex, string file)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, file);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
