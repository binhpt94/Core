using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Presentation;
using Game.MathQuiz.UseCases;
using UnityEngine;
using UnityEngine.UI;

namespace Game.MathQuiz.Presentation
{
    /// <summary>
    /// Passive Unity view. No game rules here: it renders view models and raises input events.
    /// References are set by the prefab (built by the recipe, skinned by a Variant) or by
    /// <see cref="QuizViewLayout"/> via <see cref="Bind"/>. Every reference is REQUIRED (ARCH072).
    /// IQuizView and its view models are internal to the feature, hence the explicit implementation.
    /// </summary>
    public sealed class QuizView : MonoBehaviour, IQuizView
    {
        [Header("Question")]
        [SerializeField] private Text progressText;
        [SerializeField] private Text scoreText;
        [SerializeField] private Text questionText;
        [SerializeField] private Button[] optionButtons = new Button[3];

        [Header("Timer")]
        [SerializeField] private GameObject timerRoot;
        [SerializeField] private Text timerText;
        [SerializeField] private RectTransform timerFill;

        [Header("Arena")]
        [SerializeField] private RectTransform arenaLane;
        [Tooltip("Inactive slime marker inside the lane (the slot's graybox): its size and anchors come from the layout, the view only moves x.")]
        [SerializeField] private RectTransform slimeTemplate;
        [Tooltip("Art slot MathQuiz.Slime: real art (candidate/final) replaces the graybox without touching gameplay. Empty = graybox.")]
        [OptionalReference] [SerializeField] private ArtSlotAsset slimeArt = null;
        [SerializeField] private Text livesText;
        [Tooltip("Visual smoothing of slime positions between simulation ticks (presentation only).")]
        [VisualTuning] [SerializeField] private float positionSmoothing = 14f;

        [Header("Feedback")]
        [SerializeField] private Text feedbackText;
        [SerializeField] private Button nextButton;

        [Header("Summary")]
        [SerializeField] private GameObject summaryPanel;
        [SerializeField] private Text summaryTitleText;
        [SerializeField] private Text summaryDetailText;
        [SerializeField] private Button restartButton;

        [Header("Colors")]
        [SerializeField] private Color optionNormal = new Color(0.22f, 0.45f, 0.85f);
        [SerializeField] private Color optionCorrect = new Color(0.20f, 0.70f, 0.35f);
        [SerializeField] private Color optionWrong = new Color(0.85f, 0.25f, 0.25f);
        [SerializeField] private Color optionDimmed = new Color(0.55f, 0.58f, 0.65f);

        private bool _hooked;

        // Presentation-only state: marker objects and smoothed positions. Never read by the simulation.
        private readonly Dictionary<int, RectTransform> _markers = new Dictionary<int, RectTransform>();
        private readonly Dictionary<int, float> _targets = new Dictionary<int, float>();
        private bool _raging;

        private Action<int> _optionSelected;
        private Action _nextRequested;
        private Action _restartRequested;

        event Action<int> IQuizView.OptionSelected { add => _optionSelected += value; remove => _optionSelected -= value; }
        event Action IQuizView.NextRequested { add => _nextRequested += value; remove => _nextRequested -= value; }
        event Action IQuizView.RestartRequested { add => _restartRequested += value; remove => _restartRequested -= value; }

        // Read-only access for PlayMode tests.
        public string QuestionText => questionText.text;
        public string FeedbackText => feedbackText.text;
        public int OptionCount => optionButtons.Length;
        public bool IsSummaryVisible => summaryPanel.activeSelf;
        public string TimerText => timerText.text;
        public string LivesText => livesText.text;
        public int SlimeCount => _markers.Count;
        public void SimulateOptionClick(int index) => optionButtons[index].onClick.Invoke();
        public void SimulateNextClick() => nextButton.onClick.Invoke();
        public void SimulateRestartClick() => restartButton.onClick.Invoke();

        internal void Bind(
            Text progress, Text score, Text question, Button[] options,
            GameObject timer, Text timerLabel, RectTransform timerBar,
            RectTransform lane, RectTransform slime, Text lives,
            Text feedback, Button next,
            GameObject summary, Text summaryTitle, Text summaryDetail, Button restart)
        {
            arenaLane = lane;
            slimeTemplate = slime;
            livesText = lives;
            timerRoot = timer;
            timerText = timerLabel;
            timerFill = timerBar;
            progressText = progress;
            scoreText = score;
            questionText = question;
            optionButtons = options;
            feedbackText = feedback;
            nextButton = next;
            summaryPanel = summary;
            summaryTitleText = summaryTitle;
            summaryDetailText = summaryDetail;
            restartButton = restart;
            HookButtons();
        }

        private void Awake()
        {
            if (restartButton != null)
                HookButtons(); // Inspector-wired variant.
        }

        private void HookButtons()
        {
            if (_hooked)
                return;
            _hooked = true;

            for (int i = 0; i < optionButtons.Length; i++)
            {
                int index = i; // capture copy for the closure
                optionButtons[i].onClick.AddListener(() => _optionSelected?.Invoke(index));

                // Let our own colors show when buttons are disabled after answering.
                var colors = optionButtons[i].colors;
                colors.disabledColor = Color.white;
                optionButtons[i].colors = colors;
            }

            nextButton.onClick.AddListener(() => _nextRequested?.Invoke());
            restartButton.onClick.AddListener(() => _restartRequested?.Invoke());
        }

        void IQuizView.ShowQuestion(QuestionViewModel model)
        {
            summaryPanel.SetActive(false);
            progressText.text = model.ProgressText;
            scoreText.text = model.ScoreText;
            questionText.text = model.QuestionText;

            for (int i = 0; i < optionButtons.Length; i++)
            {
                bool used = i < model.OptionTexts.Count;
                optionButtons[i].gameObject.SetActive(used);
                if (!used)
                    continue;

                SetLabel(optionButtons[i], model.OptionTexts[i]);
                optionButtons[i].image.color = optionNormal;
                optionButtons[i].interactable = true;
            }

            feedbackText.text = string.Empty;
            nextButton.gameObject.SetActive(false);
        }

        void IQuizView.ShowTimer(TimerViewModel model)
        {
            timerRoot.SetActive(model.Visible);
            if (!model.Visible)
                return;

            timerText.text = model.Text;
            timerText.color = model.IsUrgent ? optionWrong : Color.white;

            // Bar shrinks from the right: no sprite / Image.Filled setup needed.
            var max = timerFill.anchorMax;
            max.x = Mathf.Clamp01(model.Fraction);
            timerFill.anchorMax = max; // layout: dynamic
            timerFill.GetComponent<Image>().color = model.IsUrgent ? optionWrong : optionCorrect;
        }

        void IQuizView.ShowArena(ArenaViewModel model)
        {
            livesText.text = model.LivesText;
            _raging = model.Raging;

            var alive = new HashSet<int>();
            foreach (var e in model.Enemies)
            {
                alive.Add(e.Id);
                _targets[e.Id] = e.Position;
                if (!_markers.ContainsKey(e.Id))
                    _markers[e.Id] = CreateMarker(e.Id, e.Position);
            }

            var gone = new List<int>();
            foreach (var id in _markers.Keys)
                if (!alive.Contains(id))
                    gone.Add(id);
            foreach (var id in gone)
            {
                Destroy(_markers[id].gameObject); // zapped or reached the base: a VFX hook would go here
                _markers.Remove(id);
                _targets.Remove(id);
            }
        }

        private void Update()
        {
            // Smooth 20 Hz simulation positions for display. Purely visual: frame rate never affects gameplay.
            float k = 1f - Mathf.Exp(-positionSmoothing * Time.unscaledDeltaTime);
            foreach (var pair in _markers)
            {
                var rt = pair.Value;
                MoveX(rt, Mathf.Lerp(rt.anchorMin.x, _targets[pair.Key], k));
                rt.GetComponent<Image>().color = _raging ? optionWrong : optionCorrect;
            }
        }

        private RectTransform CreateMarker(int id, float position)
        {
            var art = slimeArt != null ? slimeArt.Resolve(out _) : null;
            var rt = art != null ? (RectTransform)Instantiate(art, arenaLane, false).transform : Instantiate(slimeTemplate, arenaLane);
            var anchorMin = slimeTemplate.anchorMin;
            var anchorMax = slimeTemplate.anchorMax;
            rt.anchorMin = anchorMin; // layout: dynamic
            rt.anchorMax = anchorMax; // layout: dynamic
            rt.name = "Slime" + id;
            rt.gameObject.SetActive(true);
            MoveX(rt, position);
            return rt;
        }

        /// <summary>Data-driven movement only: the size and vertical band stay as the layout made them (ARCH100).</summary>
        private static void MoveX(RectTransform rt, float x)
        {
            var min = rt.anchorMin;
            var max = rt.anchorMax;
            min.x = x;
            max.x = x;
            rt.anchorMin = min; // layout: dynamic
            rt.anchorMax = max; // layout: dynamic
        }

        void IQuizView.ShowAnswerFeedback(AnswerFeedbackViewModel model)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                optionButtons[i].interactable = false;
                optionButtons[i].image.color =
                    i == model.CorrectIndex ? optionCorrect :
                    i == model.SelectedIndex ? optionWrong :
                    optionDimmed;
            }

            feedbackText.text = model.FeedbackText;
            feedbackText.color = model.IsCorrect ? optionCorrect : optionWrong;
            scoreText.text = model.ScoreText;

            SetLabel(nextButton, model.NextButtonText);
            nextButton.gameObject.SetActive(true);
        }

        void IQuizView.ShowRoundSummary(RoundSummaryViewModel model)
        {
            summaryTitleText.text = model.TitleText;
            summaryDetailText.text = model.DetailText;
            SetLabel(restartButton, model.RestartButtonText);
            summaryPanel.SetActive(true);
        }

        private static void SetLabel(Button button, string text)
        {
            var label = button.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = text;
        }
    }
}
