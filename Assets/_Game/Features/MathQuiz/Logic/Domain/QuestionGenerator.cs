using System.Collections.Generic;
using Game.Core;

namespace Game.MathQuiz.Domain
{
    internal interface IQuestionGenerator
    {
        /// <summary>Question <paramref name="questionNumber"/> (1-based) of round <paramref name="round"/>.</summary>
        Question Next(int round, int questionNumber);
    }

    /// <summary>
    /// Generates addition questions with KEYED randomness: question N of round R always uses the streams
    /// (R, N), so it does not depend on earlier questions, and each concern has its own stream:
    /// changing how distractors are picked (or their config) never changes the operands or the shuffle.
    /// Distractors are close to the answer, never negative and never duplicate.
    /// </summary>
    internal sealed class QuestionGenerator : IQuestionGenerator
    {
        public const string OperandsStream = "quiz.operands";
        public const string DistractorsStream = "quiz.distractors";
        public const string ShuffleStream = "quiz.shuffle";

        private const int RandomAttemptsPerOption = 20;

        private readonly QuizConfig _config;
        private readonly IRandomStreams _streams;

        public QuestionGenerator(QuizConfig config, IRandomStreams streams)
        {
            _config = config ?? throw new System.ArgumentNullException(nameof(config));
            _streams = streams ?? throw new System.ArgumentNullException(nameof(streams));
        }

        public Question Next(int round, int questionNumber)
        {
            var operands = _streams.For(OperandsStream, round, questionNumber);
            int a = operands.Next(_config.MinOperand, _config.MaxOperand + 1);
            int b = operands.Next(_config.MinOperand, _config.MaxOperand + 1);
            int correct = a + b;

            var options = new List<int>(_config.OptionCount) { correct };
            AddDistractors(options, correct, _streams.For(DistractorsStream, round, questionNumber));
            Shuffle(options, _streams.For(ShuffleStream, round, questionNumber));

            return new Question(a, b, options);
        }

        private void AddDistractors(List<int> options, int correct, IRandom random)
        {
            int maxOffset = _config.MaxDistractorOffset;
            int attempts = (_config.OptionCount - 1) * RandomAttemptsPerOption;

            while (options.Count < _config.OptionCount && attempts-- > 0)
            {
                int offset = random.Next(1, maxOffset + 1);
                int candidate = random.Next(0, 2) == 0 ? correct - offset : correct + offset;
                if (candidate >= 0 && !options.Contains(candidate))
                    options.Add(candidate);
            }

            // Deterministic fallback (e.g. correct == 0 with small offset): walk upwards.
            int next = correct + 1;
            while (options.Count < _config.OptionCount)
            {
                if (!options.Contains(next))
                    options.Add(next);
                next++;
            }
        }

        private static void Shuffle(List<int> list, IRandom random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(0, i + 1);
                int tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
