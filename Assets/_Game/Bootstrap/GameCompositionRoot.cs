using System;
using System.IO;
using Game.Core;
using Game.MathQuiz.Presentation;
using Game.Progress.Infrastructure;
using UnityEngine;

namespace Game.Bootstrap
{
    /// <summary>
    /// Composition root: the ONLY place where concrete implementations are chosen and wired.
    /// The scene contains just this component (Scene-Light). It references each feature's PUBLIC
    /// asset entry (e.g. MathQuizAssets), never a feature's private prefabs or config.
    ///
    /// Determinism boundary: this is where non-deterministic inputs enter, once, and get recorded:
    ///   - the session SEED (always set, always logged; never "unseeded random");
    ///   - game time (UnityGameClock) and frames (UnityFrameSource), turned into fixed ticks by the presenter.
    /// On an error the replay is written to persistentDataPath/replays so the bug can be reproduced in dotnet test.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameCompositionRoot : MonoBehaviour
    {
        [Tooltip("Optional: the quiz runs with defaults and a code-built UI when empty.")]
        [OptionalReference] [SerializeField] private MathQuizAssets mathQuizAssets = null;

        [Tooltip("0 = new seed every launch (logged). Paste a seed from a log/replay to reproduce that session's randomness.")]
        [SerializeField] private long randomSeed = 0;

        private Lifetime _appLifetime;
        private ProgressModule _progress;
        private MathQuizModule _quiz;
        private bool _replayDumped;

        /// <summary>For PlayMode tests.</summary>
        public QuizView View => _quiz.View;

        public ulong Seed => _quiz.Seed;

        private void Awake()
        {
            IGameLogger logger = new UnityGameLogger();
            _appLifetime = new Lifetime("app", logger);

            ulong seed = randomSeed != 0 ? (ulong)randomSeed : NewSeed();
            logger.Info("session seed = " + seed + " (set GameRoot > Random Seed to reproduce)");

            UiInfrastructure.EnsureEventSystem(transform);
            _progress = ProgressInstaller.Install();
            _quiz = MathQuizInstaller.Install(mathQuizAssets, transform, new RandomStreams(seed),
                new UnityGameClock(), new UnityFrameSource(), _progress.BestScore, _appLifetime, logger);

            Application.logMessageReceived += OnLogMessage;
        }

        private void Start() => _quiz.Start();

        /// <summary>The only non-deterministic number in the game; it is logged and stored in every replay.</summary>
        private static ulong NewSeed()
        {
            ulong x = (ulong)DateTime.UtcNow.Ticks ^ ((ulong)(uint)Environment.TickCount << 32);
            return x == 0 ? 1 : x;
        }

        public string ExportReplay() => _quiz.ExportReplay();

        private void OnLogMessage(string message, string stackTrace, LogType type)
        {
            bool gameError = type == LogType.Exception || (type == LogType.Error && message.StartsWith("[Game]", StringComparison.Ordinal));
            if (!gameError || _replayDumped || _quiz == null)
                return;
            _replayDumped = true; // once per session
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "replays");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "last-error.replay.txt");
                File.WriteAllText(path, ExportReplay());
                Debug.Log("[Game] replay of the failing session saved: " + path + " (drop it into Features/MathQuiz/Tests/Replays and run dotnet test)");
            }
            catch (IOException)
            {
                // never let diagnostics break the game
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
                FlushSaves();
        }

        private void OnApplicationQuit() => FlushSaves();

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLogMessage;
            _appLifetime?.Dispose(); // plain C# object: ?. is safe here. Cancels the simulation loop.
            FlushSaves();
        }

        private void FlushSaves() => _progress?.Flush();
    }
}
