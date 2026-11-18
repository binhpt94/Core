using System.Collections;
using System.Text.RegularExpressions;
using Game.Progress.Infrastructure;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Bootstrap.Tests
{
    /// <summary>
    /// PlayMode smoke test: boots the real composition root with real Unity UI and plays one answer.
    /// Logic is covered by EditMode/dotnet tests; this only proves the wiring works.
    /// </summary>
    public sealed class GameSmokeTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => PlayerPrefs.DeleteKey(ProgressInstaller.PlayerPrefsKey);

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                Object.Destroy(_root);
            PlayerPrefs.DeleteKey(ProgressInstaller.PlayerPrefsKey);
        }

        [UnityTest]
        public IEnumerator Boot_ShowsQuestion_AndAnsweringShowsFeedback()
        {
            _root = new GameObject("GameRoot");
            var composition = _root.AddComponent<GameCompositionRoot>();

            yield return null; // Awake + Start

            var view = composition.View;
            Assert.That(view, Is.Not.Null);
            Assert.That(view.OptionCount, Is.EqualTo(3));
            Assert.That(Regex.IsMatch(view.QuestionText, @"^\d+ \+ \d+ = \?$"), Is.True, view.QuestionText);

            Assert.That(view.LivesText, Does.Contain("3"));
            Assert.That(composition.Seed, Is.Not.EqualTo(0UL), "a session always has a (logged) seed");

            view.SimulateOptionClick(0);
            yield return new WaitForSeconds(0.2f); // the click is applied at the next simulation tick

            Assert.That(view.FeedbackText, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator Restart_DuringCountdown_RestartsTimer_AndOldTimerNeverFires()
        {
            _root = new GameObject("GameRoot");
            var composition = _root.AddComponent<GameCompositionRoot>();
            yield return null;
            var view = composition.View;

            Assert.That(view.TimerText, Is.EqualTo("10s"));
            yield return new WaitForSeconds(2.2f);  // real UniTask.Delay on the PlayerLoop
            Assert.That(view.TimerText, Is.EqualTo("8s"));

            view.SimulateRestartClick();
            yield return null;
            Assert.That(view.TimerText, Is.EqualTo("10s"));

            yield return new WaitForSeconds(1.2f);
            Assert.That(view.TimerText, Is.EqualTo("9s"), "only the new countdown is running");
            Assert.That(view.FeedbackText, Is.Empty);
        }
    }
}
