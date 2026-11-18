using System.Collections.Generic;
using Game.Core;
using Game.MathQuiz.Domain;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    public sealed class QuestionGeneratorTests
    {
        private const int Iterations = 2000;

        private static IEnumerable<Question> Generate(QuizConfig config, int seed, int count)
        {
            var generator = new QuestionGenerator(config, new RandomStreams((ulong)seed));
            for (int i = 0; i < count; i++)
                yield return generator.Next(1 + i / 10, 1 + i % 10);
        }

        [Test]
        [Covers("R-QUIZ-OPTIONS")]
        public void Next_HasExactlyThreeDistinctOptionsWithOneCorrect()
        {
            foreach (var q in Generate(QuizConfig.Default, 1, Iterations))
            {
                Assert.That(q.Options.Count, Is.EqualTo(3));
                Assert.That(new HashSet<int>(q.Options).Count, Is.EqualTo(3), "options must be distinct");
                Assert.That(q.Options[q.CorrectIndex], Is.EqualTo(q.OperandA + q.OperandB));
            }
        }

        [Test]
        public void Next_OperandsWithinConfiguredRange()
        {
            var config = QuizConfig.Create(5, 9, 10).Value;

            foreach (var q in Generate(config, 2, Iterations))
            {
                Assert.That(q.OperandA, Is.InRange(5, 9));
                Assert.That(q.OperandB, Is.InRange(5, 9));
            }
        }

        [Test]
        [Covers("R-QUIZ-OPTIONS")]
        public void Next_DistractorsAreNonNegativeAndClose()
        {
            var config = QuizConfig.Create(0, 10, 10, 3, 3).Value;

            foreach (var q in Generate(config, 3, Iterations))
            {
                foreach (int option in q.Options)
                {
                    Assert.That(option, Is.GreaterThanOrEqualTo(0));
                    Assert.That(System.Math.Abs(option - q.CorrectAnswer), Is.LessThanOrEqualTo(3));
                }
            }
        }

        [Test]
        public void Next_ZeroPlusZero_StillProducesValidQuestion()
        {
            var config = QuizConfig.Create(0, 0, 1, 3, 1).Value;

            foreach (var q in Generate(config, 4, 50))
            {
                Assert.That(q.CorrectAnswer, Is.EqualTo(0));
                Assert.That(new HashSet<int>(q.Options).Count, Is.EqualTo(3));
                Assert.That(q.Options, Has.All.GreaterThanOrEqualTo(0));
            }
        }

        [Test]
        [Covers("R-QUIZ-SHUFFLE")]
        public void Next_CorrectAnswerPositionIsShuffled()
        {
            var counts = new int[3];
            foreach (var q in Generate(QuizConfig.Default, 5, Iterations))
                counts[q.CorrectIndex]++;

            // Each position should get roughly a third; allow a wide margin to keep the test stable.
            foreach (int count in counts)
                Assert.That(count, Is.GreaterThan(Iterations / 5));
        }

        [Test]
        public void QuestionN_DoesNotDependOnEarlierQuestions()
        {
            var a = new QuestionGenerator(QuizConfig.Default, new RandomStreams(5));
            var b = new QuestionGenerator(QuizConfig.Default, new RandomStreams(5));
            for (int n = 1; n < 7; n++)
                a.Next(3, n);

            var viaHistory = a.Next(3, 7);
            var direct = b.Next(3, 7); // jump straight to question 7 (debugging a bug report)

            Assert.That(direct.OperandA, Is.EqualTo(viaHistory.OperandA));
            Assert.That(direct.OperandB, Is.EqualTo(viaHistory.OperandB));
            Assert.That(direct.Options, Is.EqualTo(viaHistory.Options));
        }

        [Test]
        public void ChangingDistractorRules_NeverChangesTheOperands()
        {
            // Rejection sampling consumes a variable amount of randomness; separate streams contain the drift.
            var narrow = new QuestionGenerator(QuizConfig.Create(1, 10, 10, 3, 1).Value, new RandomStreams(11));
            var wide = new QuestionGenerator(QuizConfig.Create(1, 10, 10, 3, 9).Value, new RandomStreams(11));
            for (int n = 1; n <= 50; n++)
            {
                var x = narrow.Next(1, n);
                var y = wide.Next(1, n);
                Assert.That((y.OperandA, y.OperandB), Is.EqualTo((x.OperandA, x.OperandB)), "question " + n);
            }
        }

        [Test]
        [Covers("R-QUIZ-SHUFFLE")]
        public void Next_SameSeed_IsDeterministic()
        {
            var a = new List<Question>(Generate(QuizConfig.Default, 42, 20));
            var b = new List<Question>(Generate(QuizConfig.Default, 42, 20));

            for (int i = 0; i < a.Count; i++)
            {
                Assert.That(b[i].OperandA, Is.EqualTo(a[i].OperandA));
                Assert.That(b[i].OperandB, Is.EqualTo(a[i].OperandB));
                Assert.That(b[i].Options, Is.EqualTo(a[i].Options));
            }
        }
    }
}
