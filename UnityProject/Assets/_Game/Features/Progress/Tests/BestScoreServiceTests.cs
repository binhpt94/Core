using Game.Progress;
using NUnit.Framework;
using Game.TestSupport;

namespace Game.Progress.Tests
{
    public sealed class BestScoreServiceTests
    {
        private sealed class FakeStore : IProgressStore
        {
            public ProgressData Stored;
            public int SaveCount;
            public int FlushCount;

            public ProgressData Load() => Stored;

            public void Save(ProgressData data)
            {
                Stored = data;
                SaveCount++;
            }

            public void Flush() => FlushCount++;
        }

        private static BestScoreService CreateLoaded(FakeStore store)
        {
            var service = new BestScoreService(store);
            service.Load();
            return service;
        }

        [Test]
        public void Constructor_DoesNotTouchStore()
        {
            var store = new FakeStore { Stored = new ProgressData { BestScore = 7 } };

            var service = new BestScoreService(store);

            Assert.That(service.BestScore, Is.EqualTo(0));
            Assert.That(store.SaveCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_EmptyStore_BestScoreIsZero()
        {
            var service = CreateLoaded(new FakeStore());

            Assert.That(service.BestScore, Is.EqualTo(0));
        }

        [Test]
        [Covers("R-BEST-LOAD")]
        public void Load_ExistingData_RestoresBestScore()
        {
            var service = CreateLoaded(new FakeStore { Stored = new ProgressData { BestScore = 8 } });

            Assert.That(service.BestScore, Is.EqualTo(8));
        }

        [Test]
        [Covers("R-BEST-LOAD")]
        public void Load_CorruptedNegativeScore_IsRepaired()
        {
            var service = CreateLoaded(new FakeStore { Stored = new ProgressData { BestScore = -5 } });

            Assert.That(service.BestScore, Is.EqualTo(0));
        }

        [Test]
        [Covers("R-BEST-LOAD")]
        public void Load_UnknownFutureVersion_FallsBackToDefaults()
        {
            var service = CreateLoaded(new FakeStore
            {
                Stored = new ProgressData { Version = ProgressData.CurrentVersion + 1, BestScore = 99 }
            });

            Assert.That(service.BestScore, Is.EqualTo(0));
        }

        [Test]
        [Covers("R-BEST-SAVE")]
        public void SubmitScore_Higher_IsNewBestAndSaved()
        {
            var store = new FakeStore();
            var service = CreateLoaded(store);

            bool isNewBest = service.SubmitScore(5);

            Assert.That(isNewBest, Is.True);
            Assert.That(service.BestScore, Is.EqualTo(5));
            Assert.That(store.Stored.BestScore, Is.EqualTo(5));
            Assert.That(store.SaveCount, Is.EqualTo(1));
        }

        [Test]
        [Covers("R-BEST-SAVE")]
        public void SubmitScore_LowerOrEqual_IsIgnored()
        {
            var store = new FakeStore { Stored = new ProgressData { BestScore = 5 } };
            var service = CreateLoaded(store);

            Assert.That(service.SubmitScore(5), Is.False);
            Assert.That(service.SubmitScore(3), Is.False);
            Assert.That(service.BestScore, Is.EqualTo(5));
            Assert.That(store.SaveCount, Is.EqualTo(0));
        }

        [Test]
        public void SubmitScore_DoesNotFlush()
        {
            var store = new FakeStore();
            var service = CreateLoaded(store);

            service.SubmitScore(10);

            Assert.That(store.FlushCount, Is.EqualTo(0), "Disk writes belong to app lifecycle, not to gameplay.");
        }
    }
}
