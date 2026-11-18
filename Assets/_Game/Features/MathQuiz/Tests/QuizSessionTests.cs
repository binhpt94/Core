using Game.MathQuiz.UseCases;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.MathQuiz.Tests
{
    public sealed class QuizSessionTests
    {
        private const int Correct = 1; // index of 5 in TwoPlusThree {4, 5, 6}
        private const int Wrong = 0;

        private FakeBestScoreService _bestScore;

        private QuizSession CreateSession(int questions)
        {
            _bestScore = new FakeBestScoreService();
            return new QuizSession(
                TestConfigs.Questions(questions),
                new ScriptedQuestionGenerator(ScriptedQuestionGenerator.TwoPlusThree()),
                _bestScore);
        }

        [Test]
        public void New_IsNotStarted()
        {
            var session = CreateSession(3);

            Assert.That(session.State, Is.EqualTo(QuizState.NotStarted));
            Assert.That(session.CurrentQuestion, Is.Null);
        }

        [Test]
        public void Start_ShowsFirstQuestion()
        {
            var session = CreateSession(3);

            session.Start();

            Assert.That(session.State, Is.EqualTo(QuizState.AwaitingAnswer));
            Assert.That(session.QuestionNumber, Is.EqualTo(1));
            Assert.That(session.Score, Is.EqualTo(0));
            Assert.That(session.CurrentQuestion, Is.Not.Null);
        }

        [Test]
        public void SubmitAnswer_Correct_IncreasesScore()
        {
            var session = CreateSession(3);
            session.Start();

            var result = session.SubmitAnswer(Correct);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.IsCorrect, Is.True);
            Assert.That(result.Value.CorrectIndex, Is.EqualTo(Correct));
            Assert.That(session.Score, Is.EqualTo(1));
            Assert.That(session.State, Is.EqualTo(QuizState.Answered));
        }

        [Test]
        public void SubmitAnswer_Wrong_KeepsScoreAndRevealsCorrectIndex()
        {
            var session = CreateSession(3);
            session.Start();

            var result = session.SubmitAnswer(Wrong);

            Assert.That(result.Value.IsCorrect, Is.False);
            Assert.That(result.Value.SelectedIndex, Is.EqualTo(Wrong));
            Assert.That(result.Value.CorrectIndex, Is.EqualTo(Correct));
            Assert.That(session.Score, Is.EqualTo(0));
        }

        [Test]
        [Covers("R-QUIZ-SCORE-ONCE")]
        public void SubmitAnswer_Twice_SecondIsRejected()
        {
            var session = CreateSession(3);
            session.Start();
            session.SubmitAnswer(Correct);

            var second = session.SubmitAnswer(Correct);

            Assert.That(second.IsSuccess, Is.False);
            Assert.That(second.Error.Code, Is.EqualTo(QuizSession.ErrorWrongState));
            Assert.That(session.Score, Is.EqualTo(1), "double tap must not score twice");
        }

        [TestCase(-1)]
        [TestCase(3)]
        public void SubmitAnswer_OutOfRange_IsRejected(int index)
        {
            var session = CreateSession(3);
            session.Start();

            var result = session.SubmitAnswer(index);

            Assert.That(result.Error.Code, Is.EqualTo(QuizSession.ErrorInvalidOption));
            Assert.That(session.State, Is.EqualTo(QuizState.AwaitingAnswer));
        }

        [Test]
        public void SubmitAnswer_BeforeStart_IsRejected()
        {
            var session = CreateSession(3);

            Assert.That(session.SubmitAnswer(0).Error.Code, Is.EqualTo(QuizSession.ErrorWrongState));
        }

        [Test]
        [Covers("R-TIMER-DEADLINE")]
        public void TimeOut_WhileAwaiting_CountsAsWrong()
        {
            var session = CreateSession(3);
            session.Start();

            var result = session.TimeOut();

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.IsTimeout, Is.True);
            Assert.That(result.Value.IsCorrect, Is.False);
            Assert.That(result.Value.CorrectIndex, Is.EqualTo(Correct));
            Assert.That(session.Score, Is.EqualTo(0));
            Assert.That(session.State, Is.EqualTo(QuizState.Answered));
        }

        [Test]
        public void TimeOut_AfterAnswer_IsRejected()
        {
            var session = CreateSession(3);
            session.Start();
            session.SubmitAnswer(Correct);

            var result = session.TimeOut();

            Assert.That(result.Error.Code, Is.EqualTo(QuizSession.ErrorWrongState));
            Assert.That(session.Score, Is.EqualTo(1));
        }

        [Test]
        public void Advance_BeforeAnswer_IsRejected()
        {
            var session = CreateSession(3);
            session.Start();

            var result = session.Advance();

            Assert.That(result.Error.Code, Is.EqualTo(QuizSession.ErrorWrongState));
            Assert.That(session.QuestionNumber, Is.EqualTo(1));
        }

        [Test]
        public void Advance_MidRound_GoesToNextQuestion()
        {
            var session = CreateSession(3);
            session.Start();
            session.SubmitAnswer(Correct);

            var result = session.Advance();

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.HasValue, Is.False);
            Assert.That(session.QuestionNumber, Is.EqualTo(2));
            Assert.That(session.State, Is.EqualTo(QuizState.AwaitingAnswer));
        }

        [Test]
        [Covers("R-QUIZ-ROUND")]
        public void FullRound_FinishesWithSummaryAndSubmitsScoreOnce()
        {
            var session = CreateSession(3);
            session.Start();

            RoundSummary? summary = null;
            int[] answers = { Correct, Wrong, Correct };
            foreach (int answer in answers)
            {
                session.SubmitAnswer(answer);
                summary = session.Advance().Value;
            }

            Assert.That(session.State, Is.EqualTo(QuizState.Finished));
            Assert.That(summary.HasValue, Is.True);
            Assert.That(summary.Value.Score, Is.EqualTo(2));
            Assert.That(summary.Value.TotalQuestions, Is.EqualTo(3));
            Assert.That(summary.Value.IsNewBest, Is.True);
            Assert.That(summary.Value.BestScore, Is.EqualTo(2));
            Assert.That(_bestScore.Submitted, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        [Covers("R-QUIZ-ROUND")]
        public void Start_AfterFinish_ResetsRound()
        {
            var session = CreateSession(1);
            session.Start();
            session.SubmitAnswer(Correct);
            session.Advance();

            session.Start();

            Assert.That(session.State, Is.EqualTo(QuizState.AwaitingAnswer));
            Assert.That(session.Score, Is.EqualTo(0));
            Assert.That(session.QuestionNumber, Is.EqualTo(1));
        }
    }
}
