using Game.Core;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class ResultTests
    {
        [Test]
        public void Ok_HasValue()
        {
            var result = Result<int>.Ok(5);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value, Is.EqualTo(5));
        }

        [Test]
        public void Fail_AccessingValue_Throws()
        {
            var result = Result<int>.Fail("code", "message");

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error.Code, Is.EqualTo("code"));
            Assert.Throws<System.InvalidOperationException>(() => { var _ = result.Value; });
        }
    }
}
