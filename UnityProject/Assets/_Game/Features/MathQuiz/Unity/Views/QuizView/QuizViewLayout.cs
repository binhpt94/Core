using Game.Core.Presentation;
using Game.MathQuiz.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Game.MathQuiz.Presentation
{
    /// <summary>
    /// LAYOUT RECIPE of QuizView: the only C# file allowed to set positions, sizes and anchors (ARCH100), so a
    /// "2px" change here is known to be a layout change of QuizView only (test gate: L2/L3 for this view).
    /// Every element has a MIN and a PREFERRED height: the vertical layout uses preferred heights when there is
    /// room (phones) and shrinks towards the minimums on short canvases (tablets), instead of overflowing.
    /// Used two ways:
    /// 1. as the PREFAB RECIPE: Editor/QuizViewPrefabRecipe saves its output as QuizView.generated.prefab
    ///    (artists then make a Prefab Variant for skins; regenerating the base keeps their overrides);
    /// 2. as a runtime fallback when MathQuizAssets has no prefab (empty project, tests).
    /// It does not create an EventSystem: that is app infrastructure, owned by Bootstrap.
    /// </summary>
    internal static class QuizViewLayout
    {
        private static readonly Vector2 ReferenceResolution = new Vector2(1080, 1920);
        private static readonly Color Background = new Color(0.10f, 0.12f, 0.18f);
        private static readonly Color Overlay = new Color(0f, 0f, 0f, 0.8f);
        private static readonly Color Accent = new Color(0.95f, 0.65f, 0.15f);

        public static QuizView Create(Transform parent = null)
        {
            Font font = LoadFont();

            var canvasGo = new GameObject("QuizCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(parent, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            var root = CreatePanel("Background", canvasGo.transform, Background);
            Stretch(root);
            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            // Top/bottom clear the largest safe-area insets of the standard devices (notch phone: ≈118 top, ≈85 bottom
            // in canvas units) with a small margin; the tablet still fits all minimum heights.
            layout.padding = new RectOffset(60, 60, 128, 96);
            layout.spacing = 40;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Header: progress (left) + score (right)
            var header = CreateRow("Header", root, 0, 90, 100);
            var progress = CreateText("Progress", header, font, 56, TextAnchor.MiddleLeft, Color.white);
            var score = CreateText("Score", header, font, 56, TextAnchor.MiddleRight, Accent);

            // Timer: label + shrinking bar
            var timer = CreatePanel("Timer", root, new Color(1f, 1f, 1f, 0.08f));
            SetHeight(timer.gameObject, 70, 90);
            var timerFill = CreatePanel("Fill", timer, new Color(0.20f, 0.70f, 0.35f));
            Stretch(timerFill);
            var timerText = CreateText("Label", timer, font, 56, TextAnchor.MiddleCenter, Color.white);
            Stretch((RectTransform)timerText.transform);

            // Arena lane: base on the left (x = 0), slimes come from the right
            var lane = CreatePanel("ArenaLane", root, new Color(1f, 1f, 1f, 0.05f));
            SetHeight(lane.gameObject, 110, 140);
            var baseMarker = CreatePanel("Base", lane, Accent);
            baseMarker.anchorMin = new Vector2(0f, 0f);
            baseMarker.anchorMax = new Vector2(0.03f, 1f);
            baseMarker.offsetMin = Vector2.zero;
            baseMarker.offsetMax = Vector2.zero;
            // The slime marker is the GRAYBOX of art slot MathQuiz.Slime: generated from its contract (size, pivot), so it
            // always fits what real art must meet. Real art bound in MathQuizAssets → slot replaces it at runtime.
            var slime = Graybox.CreateUi(MathQuizArtSlots.Slime, lane);
            slime.name = "SlimeTemplate";
            slime.anchorMin = new Vector2(0f, 0.5f); // the view moves x only
            slime.anchorMax = new Vector2(0f, 0.5f);
            slime.gameObject.SetActive(false);
            var lives = CreateText("Lives", root, font, 52, TextAnchor.MiddleLeft, Accent);
            SetHeight(lives.gameObject, 70);

            var question = CreateText("Question", root, font, 160, TextAnchor.MiddleCenter, Color.white);
            ShrinkToFit(question, 90); // long sums shrink instead of clipping (the layout gate checks the MIN size fits)
            SetHeight(question.gameObject, 200, 400);

            // Three answer buttons in a row
            var optionsRow = CreateRow("Options", root, 40, 200, 260);
            var options = new Button[3];
            for (int i = 0; i < options.Length; i++)
                options[i] = CreateButton("Option" + i, optionsRow, font, 110, new Color(0.22f, 0.45f, 0.85f));

            var feedback = CreateText("Feedback", root, font, 64, TextAnchor.MiddleCenter, Color.white);
            ShrinkToFit(feedback, 40);
            SetHeight(feedback.gameObject, 90, 140);

            var next = CreateButton("Next", root, font, 64, Accent);
            SetHeight(next.gameObject, 140, 180);

            // Summary overlay
            var summary = CreatePanel("Summary", canvasGo.transform, Overlay);
            Stretch(summary);
            var summaryLayout = summary.gameObject.AddComponent<VerticalLayoutGroup>();
            summaryLayout.padding = new RectOffset(80, 80, 400, 400);
            summaryLayout.spacing = 60;
            summaryLayout.childAlignment = TextAnchor.MiddleCenter;
            summaryLayout.childControlWidth = true;
            summaryLayout.childControlHeight = true;
            summaryLayout.childForceExpandHeight = false;
            var summaryTitle = CreateText("Title", summary, font, 110, TextAnchor.MiddleCenter, Accent);
            ShrinkToFit(summaryTitle, 60);
            SetHeight(summaryTitle.gameObject, 200);
            var summaryDetail = CreateText("Detail", summary, font, 64, TextAnchor.MiddleCenter, Color.white);
            SetHeight(summaryDetail.gameObject, 260);
            var restart = CreateButton("Restart", summary, font, 72, Accent);
            SetHeight(restart.gameObject, 200);
            summary.gameObject.SetActive(false);

            var view = canvasGo.AddComponent<QuizView>();
            view.Bind(progress, score, question, options,
                timer.gameObject, timerText, timerFill,
                lane, slime, lives,
                feedback, next,
                summary.gameObject, summaryTitle, summaryDetail, restart);
            return view;
        }

        // ---------- helpers ----------


        private static Font LoadFont()
        {
#if UNITY_2022_2_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }

        private static RectTransform CreatePanel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return (RectTransform)go.transform;
        }

        private static RectTransform CreateRow(string name, Transform parent, float spacing, float minHeight, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var row = go.GetComponent<HorizontalLayoutGroup>();
            row.spacing = spacing;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = true;
            SetHeight(go, minHeight, height);
            return (RectTransform)go.transform;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, Font font, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;

            var label = CreateText("Label", go.transform, font, fontSize, TextAnchor.MiddleCenter, Color.white);
            Stretch((RectTransform)label.transform);
            return go.GetComponent<Button>();
        }

        /// <summary>Dynamic texts (numbers, localized strings) shrink down to minSize instead of overflowing.</summary>
        private static void ShrinkToFit(Text text, int minSize)
        {
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = minSize;
            text.resizeTextMaxSize = text.fontSize;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void SetHeight(GameObject go, float height) => SetHeight(go, height, height);

        private static void SetHeight(GameObject go, float minHeight, float height)
        {
            // Never use ?? or ?. on UnityEngine.Object: Unity's fake-null objects defeat them.
            var element = go.GetComponent<LayoutElement>();
            if (element == null)
                element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = minHeight;
        }
    }
}
