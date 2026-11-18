using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// `test-gate.sh new-view Feature ViewName`: a view (screen, popup, HUD, component) that is testable in isolation
/// from the first minute and passes ARCH057/070/100/101:
///   Logic/UseCases/I&lt;V&gt;View.cs         passive view interface + view model
///   Unity/Views/&lt;V&gt;/&lt;V&gt;.cs             MonoBehaviour (renders view models, raises events; no layout constants)
///   Unity/Views/&lt;V&gt;/&lt;V&gt;Layout.cs       layout recipe (the only place for positions/sizes)
///   DevTools/Fixtures/&lt;V&gt;Fixtures.cs   smoke + edge fixtures (typed)
///   Editor/Views/&lt;V&gt;Cases.cs            how the layout gate / preview window create it
/// Missing DevTools/Editor asmdefs and InternalsVisibleTo lines are added.
/// </summary>
internal static class ViewScaffold
{
    public static int NewView(string root, string[] args)
    {
        string feature = args.Length > 1 ? args[1] : null;
        string view = args.Length > 2 ? args[2] : null;
        if (feature == null || view == null || !Regex.IsMatch(feature, "^[A-Z][A-Za-z0-9]+$") || !Regex.IsMatch(view, "^[A-Z][A-Za-z0-9]+$"))
        {
            Console.Error.WriteLine("Usage: new-view <Feature> <PascalCaseViewName>   e.g. new-view MathQuiz ResultPopup");
            return 64;
        }

        string dir = Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", feature);
        if (!Directory.Exists(Path.Combine(dir, "Logic")) || !Directory.Exists(Path.Combine(dir, "Unity")))
        {
            Console.Error.WriteLine("Feature " + feature + " needs Logic/ and Unity/ (run new-feature first).");
            return 1;
        }
        if (Directory.Exists(Path.Combine(dir, "Unity", "Views", view)))
        {
            Console.Error.WriteLine("View already exists: " + view);
            return 1;
        }

        string iface = "I" + (view.EndsWith("View", StringComparison.Ordinal) ? view : view + "View");
        string model = view + "Model";
        string ns = "Game." + feature;

        Write(dir, "Logic/UseCases/" + iface + ".cs", $@"using System;

namespace {ns}.UseCases
{{
    /// <summary>Passive view: shows what it is told, forwards input as events. Implemented by {view} and test fakes.</summary>
    internal interface {iface}
    {{
        event Action CloseRequested;

        void Show({model} model);
    }}

    /// <summary>Everything {view} displays, pre-formatted (formatting is unit-tested in the presenter).</summary>
    internal sealed class {model}
    {{
        public string TitleText;
        public string BodyText;
        public string CloseButtonText;
    }}
}}
");

        Write(dir, "Unity/Views/" + view + "/" + view + ".cs", $@"using System;
using {ns}.UseCases;
using UnityEngine;
using UnityEngine.UI;

namespace {ns}.Presentation
{{
    /// <summary>Passive view. Layout lives in {view}Layout (or the prefab), never here (ARCH100).</summary>
    public sealed class {view} : MonoBehaviour, {iface}
    {{
        [SerializeField] private Text titleText;
        [SerializeField] private Text bodyText;
        [SerializeField] private Button closeButton;

        private Action _closeRequested;
        private bool _hooked;

        event Action {iface}.CloseRequested {{ add => _closeRequested += value; remove => _closeRequested -= value; }}

        internal void Bind(Text title, Text body, Button close)
        {{
            titleText = title;
            bodyText = body;
            closeButton = close;
            Hook();
        }}

        private void Awake()
        {{
            if (closeButton != null)
                Hook(); // prefab-wired
        }}

        private void Hook()
        {{
            if (_hooked)
                return;
            _hooked = true;
            closeButton.onClick.AddListener(() => _closeRequested?.Invoke());
        }}

        void {iface}.Show({model} model)
        {{
            titleText.text = model.TitleText;
            bodyText.text = model.BodyText;
            var label = closeButton.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = model.CloseButtonText;
        }}
    }}
}}
");

        Write(dir, "Unity/Views/" + view + "/" + view + "Layout.cs", $@"using UnityEngine;
using UnityEngine.UI;

namespace {ns}.Presentation
{{
    /// <summary>LAYOUT RECIPE of {view}: the only C# allowed to set positions/sizes (ARCH100). Min + preferred heights.</summary>
    internal static class {view}Layout
    {{
        public static {view} Create(Transform parent = null)
        {{
#if UNITY_2022_2_OR_NEWER
            var font = Resources.GetBuiltinResource<Font>(""LegacyRuntime.ttf"");
#else
            var font = Resources.GetBuiltinResource<Font>(""Arial.ttf"");
#endif
            var canvasGo = new GameObject(""{view}Canvas"", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(parent, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            var panel = new GameObject(""Panel"", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(canvasGo.transform, false);
            panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);
            var rt = (RectTransform)panel.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(80, 80, 300, 300);
            layout.spacing = 40;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(""Title"", panel.transform, font, 96, 60, 160);
            var body = CreateText(""Body"", panel.transform, font, 56, 120, 400);
            var close = new GameObject(""Close"", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            close.transform.SetParent(panel.transform, false);
            close.GetComponent<LayoutElement>().minHeight = 140;
            close.GetComponent<LayoutElement>().preferredHeight = 180;
            var label = CreateText(""Label"", close.transform, font, 64, 0, 0);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;

            var v = canvasGo.AddComponent<{view}>();
            v.Bind(title, body, close.GetComponent<Button>());
            return v;
        }}

        private static Text CreateText(string name, Transform parent, Font font, int size, float minHeight, float height)
        {{
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.raycastTarget = false;
            t.resizeTextForBestFit = true; // dynamic text shrinks instead of clipping
            t.resizeTextMinSize = size / 2;
            t.resizeTextMaxSize = size;
            if (height > 0)
            {{
                var e = go.AddComponent<LayoutElement>();
                e.minHeight = minHeight;
                e.preferredHeight = height;
            }}
            return t;
        }}
    }}
}}
");

        Write(dir, "DevTools/Fixtures/" + view + "Fixtures.cs", $@"using System.Collections.Generic;
using Game.Core.DevTools;
using {ns}.UseCases;

namespace {ns}.DevTools
{{
    /// <summary>States of {view} for the layout gate and the preview window. Add scenario fixtures once a presenter exists.</summary>
    internal static class {view}Fixtures
    {{
        public static IReadOnlyList<ViewFixture<{iface}>> All() => new[]
        {{
            new ViewFixture<{iface}>(FixtureTags.Smoke, v => v.Show(new {model} {{ TitleText = ""Title"", BodyText = ""Body"", CloseButtonText = ""OK"" }}), FixtureTags.Smoke),
            new ViewFixture<{iface}>(""edge-longest-texts"", v => v.Show(new {model}
            {{
                TitleText = new string('W', 40),
                BodyText = string.Join("" "", System.Linq.Enumerable.Repeat(""Lorem ipsum dolor"", 20)),
                CloseButtonText = ""Close and continue""
            }}), FixtureTags.Edge),
        }};
    }}
}}
");

        Write(dir, "Editor/Views/" + view + "Cases.cs", $@"using System.Collections.Generic;
using Game.Core.DevTools;
using Game.Core.EditorTools;
using {ns}.DevTools;
using {ns}.Presentation;
using {ns}.UseCases;
using UnityEngine;

namespace {ns}.EditorTools
{{
    /// <summary>{view} in isolation (layout gate + Tools > Game > UI > View Preview). Switch to the shipped prefab once it exists.</summary>
    internal sealed class {view}Cases : ViewCases<{iface}>
    {{
        protected override string Feature => ""{feature}"";
        protected override string View => ""{view}"";

        protected override IReadOnlyList<ViewFixture<{iface}>> Fixtures() => {view}Fixtures.All();

        protected override {iface} CreateView(Transform parent, out GameObject root)
        {{
            var v = {view}Layout.Create(parent);
            root = v.gameObject;
            return v;
        }}
    }}
}}
");

        EnsureAsmdef(dir, "DevTools/Game." + feature + ".DevTools.asmdef", "Game." + feature + ".DevTools",
            new[] { "Game.Core", "Game.Core.DevTools", "Game." + feature }, pure: true, editorOnly: false, devTools: true);
        EnsureAsmdef(dir, "Editor/Game." + feature + ".Editor.asmdef", "Game." + feature + ".Editor",
            new[] { "Game.Core", "Game.Core.DevTools", "Game.Core.Editor", "Game." + feature, "Game." + feature + ".DevTools", "Game." + feature + ".Unity", "UnityEngine.UI" },
            pure: false, editorOnly: true, devTools: false);
        EnsureReferences(Path.Combine(dir, "Unity", "Game." + feature + ".Unity.asmdef"), new[] { "UnityEngine.UI" });
        EnsureIvt(dir, "Logic/AssemblyInfo.cs", "Game." + feature + ".Editor", "Game." + feature + ".DevTools");
        EnsureIvt(dir, "Unity/AssemblyInfo.cs", "Game." + feature + ".Editor");
        EnsureIvt(dir, "DevTools/AssemblyInfo.cs", "Game." + feature + ".Editor", "Game." + feature + ".Tests");

        Console.WriteLine("Created view " + feature + "/" + view + ". Next: write a presenter + its tests (pure), add scenario fixtures,");
        Console.WriteLine("then `test-gate.sh run`. The layout gate needs a baseline: an owner runs it once with LAYOUT_WRITE=1.");
        return 0;
    }

    private static void EnsureAsmdef(string dir, string rel, string name, string[] refs, bool pure, bool editorOnly, bool devTools)
    {
        string path = Path.Combine(dir, rel);
        if (File.Exists(path))
        {
            EnsureReferences(path, refs);
            return;
        }
        Write(dir, rel, "{\n    \"name\": \"" + name + "\",\n    \"rootNamespace\": \"" + name.Replace(".Editor", ".EditorTools") + "\",\n" +
                        "    \"references\": [\n" + string.Join(",\n", refs.Select(r => "        \"" + r + "\"")) + "\n    ],\n" +
                        "    \"includePlatforms\": [" + (editorOnly ? "\n        \"Editor\"\n    " : "") + "],\n    \"excludePlatforms\": [],\n" +
                        "    \"allowUnsafeCode\": false,\n    \"overrideReferences\": false,\n    \"precompiledReferences\": [],\n    \"autoReferenced\": false,\n" +
                        "    \"defineConstraints\": [" + (devTools ? "\n        \"UNITY_EDITOR || GAME_DEVTOOLS\"\n    " : "") + "],\n    \"versionDefines\": [],\n" +
                        "    \"noEngineReferences\": " + (pure ? "true" : "false") + "\n}\n");
    }

    private static void EnsureReferences(string asmdef, string[] refs)
    {
        if (!File.Exists(asmdef))
            return;
        string json = File.ReadAllText(asmdef);
        var m = Regex.Match(json, "\"references\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
        if (!m.Success)
            return;
        var missing = refs.Where(r => !m.Groups[1].Value.Contains("\"" + r + "\"")).ToList();
        if (missing.Count == 0)
            return;
        string inner = m.Groups[1].Value.TrimEnd();
        string add = string.Join(",", missing.Select(r => "\n        \"" + r + "\""));
        string replaced = "\"references\": [" + (inner.Trim().Length > 0 ? inner + "," : "") + add + "\n    ]";
        File.WriteAllText(asmdef, json.Substring(0, m.Index) + replaced + json.Substring(m.Index + m.Length));
    }

    private static void EnsureIvt(string dir, string rel, params string[] targets)
    {
        string path = Path.Combine(dir, rel);
        string text = File.Exists(path) ? File.ReadAllText(path) : "using System.Runtime.CompilerServices;\n\n";
        foreach (var t in targets.Where(t => !text.Contains("\"" + t + "\"")))
            text += "[assembly: InternalsVisibleTo(\"" + t + "\")]\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    private static void Write(string dir, string rel, string content)
    {
        string path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
    }
}
