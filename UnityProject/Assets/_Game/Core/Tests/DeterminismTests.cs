using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core;
using NUnit.Framework;

namespace Game.Core.Tests
{
    /// <summary>
    /// Frozen behaviour of the deterministic core. Expected values come from an INDEPENDENT reference
    /// implementation (Python, see docs/ARCHITECTURE.md §14), not from this C# code. Because these tests run
    /// in `dotnet test` (CoreCLR) and in Unity EditMode (Mono), they also prove cross-runtime equality.
    /// </summary>
    public sealed class DeterminismTests
    {
        [Test]
        public void Xoshiro128StarStar_MatchesReferenceVector()
        {
            var r = new DeterministicRandom(42);
            var actual = Enumerable.Range(0, 6).Select(_ => r.NextUInt()).ToArray();
            Assert.That(actual, Is.EqualTo(new uint[] { 1776835114, 4165204688, 17111135, 2317295270, 2792088233, 2554630222 }));
        }

        [Test]
        public void BoundedInts_MatchReferenceVector()
        {
            var r = new DeterministicRandom(42);
            var actual = Enumerable.Range(0, 8).Select(_ => r.Next(0, 100)).ToArray();
            Assert.That(actual, Is.EqualTo(new[] { 41, 96, 0, 53, 65, 59, 68, 52 }));
        }

        [Test]
        public void StreamDerivation_MatchesReferenceVector()
        {
            Assert.That(RandomStreams.Derive(42, "quiz.operands", 1, 3, 0), Is.EqualTo(0xa74c3db1559a4d88UL));
        }

        [Test]
        public void Next_StaysInRange_IncludingExtremes()
        {
            var r = new DeterministicRandom(1);
            for (int i = 0; i < 10000; i++)
            {
                Assert.That(r.Next(-5, 6), Is.InRange(-5, 5));
                Assert.That(r.Next(int.MinValue, int.MaxValue), Is.LessThan(int.MaxValue));
            }
            Assert.That(r.Next(7, 8), Is.EqualTo(7));
            Assert.Throws<ArgumentOutOfRangeException>(() => r.Next(3, 3));
        }

        [Test]
        public void Next_IsRoughlyUniform()
        {
            var r = new DeterministicRandom(99);
            var counts = new int[10];
            for (int i = 0; i < 100000; i++)
                counts[r.Next(0, 10)]++;
            foreach (var c in counts)
                Assert.That(c, Is.InRange(9500, 10500));
        }

        [Test]
        public void Streams_AreIndependentOfConsumptionElsewhere()
        {
            var streams = new RandomStreams(123);
            int first = streams.For("arena.spawn", 1, 5).Next(0, 1000);

            // Heavy use of other streams in between must not change it.
            for (int i = 0; i < 1000; i++)
                streams.For("quiz.operands", 1, i).Next(0, 10);

            Assert.That(streams.For("arena.spawn", 1, 5).Next(0, 1000), Is.EqualTo(first));
            Assert.That(streams.For("arena.spawn", 1, 6).Next(0, 1000000), Is.Not.EqualTo(streams.For("arena.spawn", 1, 5).Next(0, 1000000)));
            Assert.That(new RandomStreams(124).For("arena.spawn", 1, 5).Next(0, 1000000), Is.Not.EqualTo(streams.For("arena.spawn", 1, 5).Next(0, 1000000)));
        }

        [Test]
        public void StateHash_IsOrderSensitiveAndStable()
        {
            string a = new StateHashBuilder().Add(1).Add(2).Add("x").Hex;
            Assert.That(new StateHashBuilder().Add(1).Add(2).Add("x").Hex, Is.EqualTo(a));
            Assert.That(new StateHashBuilder().Add(2).Add(1).Add("x").Hex, Is.Not.EqualTo(a));
            Assert.That(new StateHashBuilder().Add("ab").Add("c").Hex, Is.Not.EqualTo(new StateHashBuilder().Add("a").Add("bc").Hex), "strings are length-prefixed");
            Assert.That(new StateHashBuilder().Hex, Is.EqualTo("cbf29ce484222325"));
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(144)]
        [TestCase(17)]
        public void TickScheduler_RunsTheSameTicksAtAnyFrameRate(int fps)
        {
            var s = new TickScheduler(20);
            s.Advance(0);
            long now = 0;
            int ticks = 0;
            long frame = 1_000_000 / fps;
            while (now + frame <= 10_000_000)
            {
                now += frame;
                ticks += s.Advance(now);
            }
            ticks += s.Advance(10_000_000);
            Assert.That(ticks, Is.EqualTo(200), "10 s at 20 Hz");
        }

        [Test]
        public void TickScheduler_LongFrame_IsClampedAndBacklogIsCaughtUp()
        {
            var s = new TickScheduler(20, maxTicksPerFrame: 3, maxFrameMicroseconds: 250_000);
            s.Advance(0);

            Assert.That(s.Advance(10_000_000), Is.EqualTo(3), "app resumed after 10 s: only 250 ms is simulated, 3 ticks now");
            Assert.That(s.Advance(10_000_000), Is.EqualTo(2), "remaining backlog of the 5 due ticks");
            Assert.That(s.Advance(10_000_000), Is.EqualTo(0));
        }

        [Test]
        public void TickScheduler_RejectsRatesThatDoNotDivideASecond()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TickScheduler(30));
        }

        // ---------- replay core with a tiny fake simulation ----------

        private sealed class Counter : IReplayableSimulation
        {
            public long Tick { get; private set; }
            public int Value;
            private readonly List<int> _pending = new List<int>();
            public bool Enqueue(string command)
            {
                if (!command.StartsWith("add ", StringComparison.Ordinal)) return false;
                _pending.Add(int.Parse(command.Substring(4)));
                return true;
            }
            public void Step() { Tick++; foreach (var p in _pending) Value += p; _pending.Clear(); Value = Value * 31 % 1000003; }
            public string StateHash() => new StateHashBuilder().Add(Tick).Add(Value).Hex;
        }

        private static string Record(params (long Tick, int Add)[] commands)
        {
            var sim = new Counter();
            var log = new ReplayLog();
            log.Meta["sim"] = "counter";
            long last = commands.Max(c => c.Tick) + 3;
            while (sim.Tick < last)
            {
                long next = sim.Tick + 1;
                foreach (var c in commands.Where(c => c.Tick == next)) // all commands of a tick, then one Step
                {
                    sim.Enqueue("add " + c.Add);
                    log.RecordCommand(next, "add " + c.Add);
                }
                sim.Step();
                log.RecordHash(sim.Tick, sim.StateHash());
            }
            log.End(sim.Tick, sim.StateHash());
            return log.Serialize();
        }

        [Test]
        public void Replay_RoundTrip_Matches()
        {
            string text = Record((3, 5), (7, 2), (7, 4));
            var result = ReplayRunner.Run(ReplayLog.Parse(text), _ => new Counter());
            Assert.That(result.Matches, Is.True, result.ToString());
            Assert.That(ReplayLog.Parse(text).Serialize(), Is.EqualTo(text), "serialize(parse(x)) == x");
        }

        [Test]
        public void Replay_ReportsTheFirstDivergentTick()
        {
            string text = Record((3, 5), (7, 2)).Replace("cmd 7 add 2", "cmd 7 add 3");
            var result = ReplayRunner.Run(ReplayLog.Parse(text), _ => new Counter());
            Assert.That(result.Matches, Is.False);
            Assert.That(result.DivergedAtTick, Is.EqualTo(7));
        }

        [Test]
        public void Replay_RingBuffer_IsMarkedTruncated()
        {
            var log = new ReplayLog { Capacity = 2 };
            log.RecordCommand(1, "a"); log.RecordCommand(2, "b"); log.RecordCommand(3, "c");
            Assert.That(log.Truncated, Is.True);
            Assert.That(ReplayRunner.Run(log, _ => new Counter()).Matches, Is.False);
        }

        [Test]
        public void Replay_RejectsUnknownFormat()
        {
            Assert.Throws<FormatException>(() => ReplayLog.Parse("replay 99\n"));
        }
    }
}
