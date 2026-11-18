using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Core;

namespace Game.MathQuiz.Domain
{
    /// <summary>
    /// Immutable, validated quiz settings. Create via <see cref="Create"/>; invalid input
    /// (e.g. from a mis-configured ScriptableObject) becomes a failed Result, not a crash.
    /// </summary>
    internal sealed class QuizConfig
    {
        public const int MaxOperandLimit = 1_000_000;
        public const int MaxSecondsPerQuestion = 600;

        public int MinOperand { get; }
        public int MaxOperand { get; }
        public int QuestionsPerRound { get; }
        public int OptionCount { get; }

        /// <summary>Wrong options are within ±MaxDistractorOffset of the correct answer.</summary>
        public int MaxDistractorOffset { get; }

        /// <summary>Time limit per question. 0 = no timer.</summary>
        public int SecondsPerQuestion { get; }

        public bool HasTimer => SecondsPerQuestion > 0;

        public static QuizConfig Default { get; } = new QuizConfig(1, 10, 10, 3, 3, 10);

        private QuizConfig(int minOperand, int maxOperand, int questionsPerRound, int optionCount,
            int maxDistractorOffset, int secondsPerQuestion)
        {
            SecondsPerQuestion = secondsPerQuestion;
            MinOperand = minOperand;
            MaxOperand = maxOperand;
            QuestionsPerRound = questionsPerRound;
            OptionCount = optionCount;
            MaxDistractorOffset = maxDistractorOffset;
        }

        public static Result<QuizConfig> Create(
            int minOperand,
            int maxOperand,
            int questionsPerRound,
            int optionCount = 3,
            int maxDistractorOffset = 3,
            int secondsPerQuestion = 10)
        {
            if (minOperand < 0)
                return Result<QuizConfig>.Fail("config.min_negative", "MinOperand must be >= 0.");
            if (maxOperand < minOperand)
                return Result<QuizConfig>.Fail("config.range", "MaxOperand must be >= MinOperand.");
            if (maxOperand > MaxOperandLimit)
                return Result<QuizConfig>.Fail("config.max_too_large", "MaxOperand must be <= " + MaxOperandLimit + ".");
            if (questionsPerRound <= 0)
                return Result<QuizConfig>.Fail("config.questions", "QuestionsPerRound must be > 0.");
            if (optionCount < 2)
                return Result<QuizConfig>.Fail("config.options", "OptionCount must be >= 2.");
            if (maxDistractorOffset < 1)
                return Result<QuizConfig>.Fail("config.offset", "MaxDistractorOffset must be >= 1.");
            if (secondsPerQuestion < 0 || secondsPerQuestion > MaxSecondsPerQuestion)
                return Result<QuizConfig>.Fail("config.timer", "SecondsPerQuestion must be 0.." + MaxSecondsPerQuestion + ".");

            return Result<QuizConfig>.Ok(
                new QuizConfig(minOperand, maxOperand, questionsPerRound, optionCount, maxDistractorOffset, secondsPerQuestion));
        }
    
        /// <summary>Canonical text for replays: "min:1,max:10,questions:10,options:3,offset:3,seconds:10".</summary>
        public string Canonical() =>
            "min:" + MinOperand + ",max:" + MaxOperand + ",questions:" + QuestionsPerRound + ",options:" + OptionCount +
            ",offset:" + MaxDistractorOffset + ",seconds:" + SecondsPerQuestion;

        public static Result<QuizConfig> FromCanonical(string text)
        {
            try
            {
                var kv = text.Split(',').Select(p => p.Split(':')).ToDictionary(p => p[0], p => int.Parse(p[1], CultureInfo.InvariantCulture));
                return Create(kv["min"], kv["max"], kv["questions"], kv["options"], kv["offset"], kv["seconds"]);
            }
            catch (Exception e) when (e is FormatException || e is KeyNotFoundException || e is IndexOutOfRangeException)
            {
                return Result<QuizConfig>.Fail("config.canonical", "bad quiz config '" + text + "'");
            }
        }
    }
}
