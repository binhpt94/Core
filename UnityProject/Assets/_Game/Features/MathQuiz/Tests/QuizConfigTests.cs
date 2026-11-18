using Game.MathQuiz.Domain;
using NUnit.Framework;

namespace Game.MathQuiz.Tests
{
    public sealed class QuizConfigTests
    {
        [Test]
        public void Default_IsThreeOptions()
        {
            Assert.That(QuizConfig.Default.OptionCount, Is.EqualTo(3));
        }

        [Test]
        public void Create_Valid_Succeeds()
        {
            var result = QuizConfig.Create(0, 20, 5);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.MaxOperand, Is.EqualTo(20));
        }

        [Test]
        public void Timer_ZeroDisablesIt()
        {
            Assert.That(QuizConfig.Create(1, 10, 5, 3, 3, 0).Value.HasTimer, Is.False);
            Assert.That(QuizConfig.Default.HasTimer, Is.True);
        }

        [TestCase(-1)]
        [TestCase(QuizConfig.MaxSecondsPerQuestion + 1)]
        public void Create_InvalidTimer_Fails(int seconds)
        {
            Assert.That(QuizConfig.Create(1, 10, 5, 3, 3, seconds).Error.Code, Is.EqualTo("config.timer"));
        }

        [TestCase(-1, 10, 5, 3, 3, "config.min_negative")]
        [TestCase(10, 5, 5, 3, 3, "config.range")]
        [TestCase(0, 2_000_000, 5, 3, 3, "config.max_too_large")]
        [TestCase(1, 10, 0, 3, 3, "config.questions")]
        [TestCase(1, 10, 5, 1, 3, "config.options")]
        [TestCase(1, 10, 5, 3, 0, "config.offset")]
        public void Create_Invalid_FailsWithCode(int min, int max, int questions, int options, int offset, string code)
        {
            var result = QuizConfig.Create(min, max, questions, options, offset);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error.Code, Is.EqualTo(code));
        }
    }
}
