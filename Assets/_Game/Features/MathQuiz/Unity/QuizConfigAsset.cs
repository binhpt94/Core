using Game.Core;
using Game.MathQuiz.Domain;
using Game.MathQuiz.UseCases;
using UnityEngine;

namespace Game.MathQuiz.Presentation
{
    /// <summary>
    /// Designer-editable settings. Data only: converted to the pure <see cref="QuizConfig"/>
    /// before it reaches Logic. Create via Assets > Create > Game > Math Quiz Config.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Math Quiz Config", fileName = "MathQuizConfig")]
    public sealed class QuizConfigAsset : ScriptableObject
    {
        [Min(0)] [SerializeField] private int minOperand = 1;
        [Min(0)] [SerializeField] private int maxOperand = 10;
        [Min(1)] [SerializeField] private int questionsPerRound = 10;
        [Min(1)] [SerializeField] private int maxDistractorOffset = 3;

        [Tooltip("Seconds per question. 0 = no timer.")]
        [Range(0, QuizConfig.MaxSecondsPerQuestion)] [SerializeField] private int secondsPerQuestion = 10;

        [Header("Arena (Quiz Defense)")]
        [Min(1)] [SerializeField] private int baseLives = 3;
        [Tooltip("Seconds between natural slime spawns (random in range).")]
        [SerializeField] private Vector2 spawnEverySeconds = new Vector2(2f, 4f);
        [Tooltip("Lane units (1000 = whole lane) per second.")]
        [SerializeField] private Vector2Int slimeSpeedPerSecond = new Vector2Int(80, 160);
        [Range(0, 100)] [SerializeField] private int hopPercent = 25;

        /// <summary>Designer units (seconds, per second) → simulation units (ticks, per tick). Rounding happens HERE, once.</summary>
        internal Result<ArenaConfig> ToArenaConfig() => Designer().ToArenaConfig();

        /// <summary>The designer values as plain data: the design-lint tests convert and check the same numbers.</summary>
        internal DesignerArena Designer() =>
            new DesignerArena(baseLives, spawnEverySeconds.x, spawnEverySeconds.y, slimeSpeedPerSecond.x, slimeSpeedPerSecond.y, hopPercent);

        internal Result<QuizConfig> ToConfig() =>
            QuizConfig.Create(minOperand, maxOperand, questionsPerRound, 3, maxDistractorOffset, secondsPerQuestion);

        private void OnValidate()
        {
            if (maxOperand < minOperand)
                maxOperand = minOperand;
        }
    }
}
