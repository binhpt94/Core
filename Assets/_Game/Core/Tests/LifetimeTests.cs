using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Core;
using NUnit.Framework;

namespace Game.Core.Tests
{
    public sealed class LifetimeTests
    {
        private sealed class RecordingLogger : IGameLogger
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public void Info(string message) { }
            public void Warning(string message) => Warnings.Add(message);
            public void Error(string message) => Errors.Add(message);
        }

        private RecordingLogger _logger;
        private Lifetime _lifetime;

        [SetUp]
        public void SetUp()
        {
            _logger = new RecordingLogger();
            _lifetime = new Lifetime("test", _logger);
        }

        [TearDown]
        public void TearDown() => _lifetime.Dispose();

        [Test]
        public void Run_CompletesNormally_ActiveCountReturnsToZero()
        {
            var gate = new UniTaskCompletionSource();
            bool finished = false;

            _lifetime.Run(async token =>
            {
                await gate.Task;
                finished = true;
            });
            Assert.That(_lifetime.ActiveTaskCount, Is.EqualTo(1));

            gate.TrySetResult();

            Assert.That(finished, Is.True);
            Assert.That(_lifetime.ActiveTaskCount, Is.EqualTo(0));
        }

        [Test]
        public void Dispose_CancelsRunningTask_Silently()
        {
            bool reachedAfterAwait = false;

            _lifetime.Run(async token =>
            {
                await UniTask.Never(token); // completes only via cancellation
                reachedAfterAwait = true;
            });

            _lifetime.Dispose();

            Assert.That(reachedAfterAwait, Is.False);
            Assert.That(_lifetime.ActiveTaskCount, Is.EqualTo(0));
            Assert.That(_logger.Errors, Is.Empty, "cancellation is not an error");
        }

        [Test]
        public void Token_IsSafeToReadAfterDispose()
        {
            // Regression for the classic bug: reading CancellationTokenSource.Token after Dispose throws.
            _lifetime.Dispose();

            Assert.DoesNotThrow(() => { var _ = _lifetime.Token.IsCancellationRequested; });
            Assert.That(_lifetime.Token.IsCancellationRequested, Is.True);
        }

        [Test]
        public void Run_Exception_IsLoggedNotThrown()
        {
            var gate = new UniTaskCompletionSource();
            _lifetime.Run(async token =>
            {
                await gate.Task;
                throw new InvalidOperationException("boom");
            });

            Assert.DoesNotThrow(() => gate.TrySetResult());

            Assert.That(_logger.Errors.Count, Is.EqualTo(1));
            Assert.That(_logger.Errors[0], Does.Contain("boom"));
            Assert.That(_lifetime.ActiveTaskCount, Is.EqualTo(0));
        }

        [Test]
        public void Run_SynchronousException_IsLogged()
        {
            _lifetime.Run(token => throw new InvalidOperationException("sync boom"));

            Assert.That(_logger.Errors.Count, Is.EqualTo(1));
            Assert.That(_logger.Errors[0], Does.Contain("sync boom"));
        }

        [Test]
        public void Run_AfterDispose_IsIgnoredWithWarning()
        {
            _lifetime.Dispose();
            bool ran = false;

            _lifetime.Run(token => { ran = true; return UniTask.CompletedTask; });

            Assert.That(ran, Is.False);
            Assert.That(_logger.Warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Own_DisposesInReverseOrder()
        {
            var order = new List<int>();
            _lifetime.Own(() => order.Add(1));
            _lifetime.Own(() => order.Add(2));
            _lifetime.Own(() => order.Add(3));

            _lifetime.Dispose();

            Assert.That(order, Is.EqualTo(new[] { 3, 2, 1 }));
        }

        [Test]
        public void Own_AfterDispose_DisposesImmediately()
        {
            _lifetime.Dispose();
            bool disposed = false;

            _lifetime.Own(() => disposed = true);

            Assert.That(disposed, Is.True);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            int count = 0;
            _lifetime.Own(() => count++);

            _lifetime.Dispose();
            _lifetime.Dispose();

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Child_EndsWithParent()
        {
            var child = _lifetime.CreateChild("child");
            bool childCancelled = false;
            child.Run(async token =>
            {
                try { await UniTask.Never(token); }
                finally { childCancelled = token.IsCancellationRequested; }
            });

            _lifetime.Dispose();

            Assert.That(child.IsAlive, Is.False);
            Assert.That(childCancelled, Is.True);
            Assert.That(_logger.Errors, Is.Empty);
        }

        [Test]
        public void Child_DisposedEarly_DoesNotAffectParent()
        {
            var child = _lifetime.CreateChild("child");

            child.Dispose();

            Assert.That(_lifetime.IsAlive, Is.True);
            Assert.That(_lifetime.Token.IsCancellationRequested, Is.False);
            Assert.DoesNotThrow(() => _lifetime.Dispose(), "parent must not re-dispose a removed child");
        }

        [Test]
        public void CreateChild_OnDisposedLifetime_Throws()
        {
            _lifetime.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _lifetime.CreateChild("late"));
        }
    }
}
