using Game.MathQuiz.Domain;
using NUnit.Framework;

namespace Game.MathQuiz.Tests
{
    public sealed class QuestionTests
    {
        [Test]
        public void Constructor_FindsCorrectIndex()
        {
            var question = new Question(2, 3, new[] { 7, 4, 5 });

            Assert.That(question.CorrectAnswer, Is.EqualTo(5));
            Assert.That(question.CorrectIndex, Is.EqualTo(2));
            Assert.That(question.IsCorrect(2), Is.True);
            Assert.That(question.IsCorrect(0), Is.False);
        }

        [Test]
        public void Constructor_WithoutCorrectAnswer_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => new Question(2, 3, new[] { 1, 2, 3 }));
        }

        [Test]
        public void Constructor_DuplicateOptions_Throws()
        {
            Assert.Throws<System.ArgumentException>(() => new Question(2, 3, new[] { 5, 5, 6 }));
        }

        [Test]
        public void Options_AreCopied_NotAliased()
        {
            var source = new[] { 4, 5, 6 };
            var question = new Question(2, 3, source);

            source[1] = 99;

            Assert.That(question.Options[1], Is.EqualTo(5));
        }

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        public void IsValidOption_ChecksRange(int index, bool expected)
        {
            var question = ScriptedQuestionGenerator.TwoPlusThree();

            Assert.That(question.IsValidOption(index), Is.EqualTo(expected));
        }
    }
}
