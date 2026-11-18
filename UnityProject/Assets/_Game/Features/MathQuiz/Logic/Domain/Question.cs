using System.Collections.Generic;

namespace Game.MathQuiz.Domain
{
    /// <summary>
    /// One addition question "A + B = ?" with N answer options, exactly one of which is correct.
    /// Immutable.
    /// </summary>
    internal sealed class Question
    {
        private readonly int[] _options;

        public int OperandA { get; }
        public int OperandB { get; }
        public int CorrectAnswer => OperandA + OperandB;
        public int CorrectIndex { get; }
        public IReadOnlyList<int> Options => _options;

        public Question(int operandA, int operandB, IReadOnlyList<int> options)
        {
            if (options == null)
                throw new System.ArgumentNullException(nameof(options));

            OperandA = operandA;
            OperandB = operandB;
            _options = new int[options.Count];

            int correctIndex = -1;
            var seen = new HashSet<int>();
            for (int i = 0; i < options.Count; i++)
            {
                _options[i] = options[i];

                if (!seen.Add(options[i]))
                    throw new System.ArgumentException("Options must be distinct.", nameof(options));

                if (options[i] == operandA + operandB)
                    correctIndex = i;
            }

            if (correctIndex < 0)
                throw new System.ArgumentException("Options must contain the correct answer.", nameof(options));

            CorrectIndex = correctIndex;
        }

        public bool IsValidOption(int index) => index >= 0 && index < _options.Length;

        public bool IsCorrect(int index) => index == CorrectIndex;
    }
}
