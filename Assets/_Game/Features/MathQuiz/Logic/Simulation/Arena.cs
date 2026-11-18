using System.Collections.Generic;
using Game.Core;
using Game.MathQuiz.Domain;

namespace Game.MathQuiz.Simulation
{
    internal sealed class Enemy
    {
        public int Id { get; }
        public int X { get; set; }
        public int Speed { get; }

        public Enemy(int id, int x, int speed)
        {
            Id = id;
            X = x;
            Speed = speed;
        }
    }

    internal enum ArenaEventKind
    {
        Spawned,
        Zapped,
        BaseHit
    }

    internal readonly struct ArenaEvent
    {
        public ArenaEventKind Kind { get; }
        public int EnemyId { get; }

        public ArenaEvent(ArenaEventKind kind, int enemyId)
        {
            Kind = kind;
            EnemyId = enemyId;
        }
    }

    /// <summary>
    /// The slime lane. All randomness is KEYED, so it never depends on call order or on other systems:
    ///   arena.spawn   (round, spawnIndex)          speed + delay until the next natural spawn
    ///   arena.penalty (round, penaltyIndex)        speed of slimes spawned by wrong answers
    ///   arena.hop     (round, enemyId, tick)       does this slime hop in place this tick?
    /// Enemies are kept and processed in ascending id order (= spawn order): deterministic iteration.
    /// </summary>
    internal sealed class Arena
    {
        public const string SpawnStream = "arena.spawn";
        public const string PenaltyStream = "arena.penalty";
        public const string HopStream = "arena.hop";

        private readonly ArenaConfig _config;
        private readonly IRandomStreams _streams;
        private readonly List<Enemy> _enemies = new List<Enemy>();

        public IReadOnlyList<Enemy> Enemies => _enemies;
        public int Lives { get; private set; }
        public long NextSpawnTick { get; private set; }
        public long RageUntilTick { get; private set; }
        public int SpawnIndex { get; private set; }
        public int PenaltyIndex { get; private set; }
        public int NextEnemyId { get; private set; }

        public Arena(ArenaConfig config, IRandomStreams streams)
        {
            _config = config;
            _streams = streams;
        }

        public bool IsRaging(long tick) => tick < RageUntilTick;

        public void Reset(int round, long tick)
        {
            _enemies.Clear();
            Lives = _config.BaseLives;
            SpawnIndex = 0;
            PenaltyIndex = 0;
            NextEnemyId = 1;
            RageUntilTick = 0;
            NextSpawnTick = tick + _streams.For(SpawnStream, round, -1).Next(_config.SpawnMinTicks / 2, _config.SpawnMinTicks + 1);
        }

        /// <summary>One tick of the world: natural spawns, then movement in id order, then base hits.</summary>
        public void Step(int round, long tick, List<ArenaEvent> events)
        {
            if (tick >= NextSpawnTick)
            {
                var rng = _streams.For(SpawnStream, round, SpawnIndex++);
                Spawn(rng.Next(_config.SpeedMin, _config.SpeedMax + 1), events);
                NextSpawnTick = tick + rng.Next(_config.SpawnMinTicks, _config.SpawnMaxTicks + 1);
            }

            bool raging = IsRaging(tick);
            for (int i = 0; i < _enemies.Count; i++)
            {
                var e = _enemies[i];
                bool hops = _streams.For(HopStream, round, e.Id, unchecked((int)tick)).Next(0, 100) < _config.HopPercent;
                if (!hops)
                    e.X -= raging ? e.Speed * _config.RagePercent / 100 : e.Speed;
            }

            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                if (_enemies[i].X > 0)
                    continue;
                events.Add(new ArenaEvent(ArenaEventKind.BaseHit, _enemies[i].Id));
                _enemies.RemoveAt(i);
                if (Lives > 0)
                    Lives--;
            }
        }

        /// <summary>Correct answer: the slime closest to the base (lowest X, then lowest id) is destroyed.</summary>
        public int ZapNearest(List<ArenaEvent> events)
        {
            if (_enemies.Count == 0)
                return -1;
            var target = _enemies[0];
            foreach (var e in _enemies)
                if (e.X < target.X || (e.X == target.X && e.Id < target.Id))
                    target = e;
            _enemies.Remove(target);
            events.Add(new ArenaEvent(ArenaEventKind.Zapped, target.Id));
            return target.Id;
        }

        /// <summary>Wrong answer / time-out: slimes rage and extra slimes appear.</summary>
        public void Punish(int round, long tick, List<ArenaEvent> events)
        {
            RageUntilTick = tick + _config.RageTicks;
            for (int i = 0; i < _config.PenaltySpawns; i++)
                Spawn(_streams.For(PenaltyStream, round, PenaltyIndex++).Next(_config.SpeedMin, _config.SpeedMax + 1), events);
        }

        private void Spawn(int speed, List<ArenaEvent> events)
        {
            if (_enemies.Count >= _config.MaxEnemies)
                return;
            var e = new Enemy(NextEnemyId++, ArenaConfig.LaneLength, speed);
            _enemies.Add(e);
            events.Add(new ArenaEvent(ArenaEventKind.Spawned, e.Id));
        }

        public void AddToHash(StateHashBuilder h)
        {
            h.Add(Lives).Add(NextSpawnTick).Add(RageUntilTick).Add(SpawnIndex).Add(PenaltyIndex).Add(NextEnemyId).Add(_enemies.Count);
            foreach (var e in _enemies) // already in ascending id order
                h.Add(e.Id).Add(e.X).Add(e.Speed);
        }
    }
}
