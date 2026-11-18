using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Core
{
    /// <summary>
    /// Owner of everything that must die together: async tasks, event subscriptions, child lifetimes.
    /// Typical tree:  App ─┬─ Round #1 ─┬─ Question #1 (countdown task)
    ///                     │            └─ Question #2 ...
    ///                     └─ Round #2 (after replay) ...
    ///
    /// Rules (see CLAUDE.md "Async &amp; lifetime"):
    /// - The ONLY way to start fire-and-forget async work is <see cref="Run"/>.
    /// - After an await, re-check <c>token.IsCancellationRequested</c> (and state) before touching state.
    /// - Main-thread only; not thread-safe by design (Unity game logic is single-threaded).
    /// </summary>
    public sealed class Lifetime : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly IGameLogger _logger;
        private readonly Lifetime _parent;
        private bool _disposed;

        public string Name { get; }

        /// <summary>
        /// Captured once at construction, so reading it after Dispose is safe
        /// (reading <c>CancellationTokenSource.Token</c> after Dispose would throw ObjectDisposedException).
        /// </summary>
        public CancellationToken Token { get; }

        public bool IsAlive => !_disposed;

        /// <summary>Number of <see cref="Run"/> tasks still executing. Lifecycle tests assert it drops to 0.</summary>
        public int ActiveTaskCount { get; private set; }

        public Lifetime(string name, IGameLogger logger)
            : this(name, logger, null, new CancellationTokenSource())
        {
        }

        private Lifetime(string name, IGameLogger logger, Lifetime parent, CancellationTokenSource cts)
        {
            Name = name;
            _logger = logger ?? NullGameLogger.Instance;
            _parent = parent;
            _cts = cts;
            Token = cts.Token;
        }

        /// <summary>Creates a lifetime that ends when this one ends (or earlier, when disposed itself).</summary>
        public Lifetime CreateChild(string name)
        {
            if (_disposed)
                throw new ObjectDisposedException(Name, "Cannot create child '" + name + "' of a disposed lifetime.");

            var child = new Lifetime(name, _logger, this, CancellationTokenSource.CreateLinkedTokenSource(Token));
            _owned.Add(child);
            return child;
        }

        /// <summary>Disposes <paramref name="disposable"/> when this lifetime ends (immediately if already ended).</summary>
        public T Own<T>(T disposable) where T : IDisposable
        {
            if (_disposed)
                disposable.Dispose();
            else
                _owned.Add(disposable);
            return disposable;
        }

        /// <summary>Runs <paramref name="onDispose"/> when this lifetime ends. Use for event unsubscription.</summary>
        public void Own(Action onDispose) => Own(new ActionDisposable(onDispose));

        /// <summary>
        /// Starts tracked async work. Cancellation is silent; any other exception is logged, never lost.
        /// Ignored (with a warning) if the lifetime has already ended.
        /// </summary>
        public void Run(Func<CancellationToken, UniTask> work)
        {
            if (work == null)
                throw new ArgumentNullException(nameof(work));

            if (_disposed)
            {
                _logger.Warning("Run() ignored: lifetime '" + Name + "' already ended.");
                return;
            }

            RunGuarded(work).Forget(); // the single allowed Forget() in the codebase
        }

        private async UniTask RunGuarded(Func<CancellationToken, UniTask> work)
        {
            CancellationToken token = Token;
            ActiveTaskCount++;
            try
            {
                await work(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Expected when the owner ends: nothing to do.
            }
            catch (Exception e)
            {
                _logger.Error("Task in lifetime '" + Name + "' failed: " + e);
            }
            finally
            {
                ActiveTaskCount--;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Cancel first: awaiting tasks resume (synchronously with UniTask) and exit via their token checks.
            _cts.Cancel();

            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    _owned[i].Dispose();
                }
                catch (Exception e)
                {
                    _logger.Error("Dispose in lifetime '" + Name + "' failed: " + e);
                }
            }
            _owned.Clear();

            _parent?.Forget(this);

            // Safe: Token was captured; disposing a linked source also unregisters it from the parent.
            _cts.Dispose();
        }

        private void Forget(Lifetime child)
        {
            if (!_disposed)
                _owned.Remove(child);
        }

        private sealed class ActionDisposable : IDisposable
        {
            private Action _action;

            public ActionDisposable(Action action) =>
                _action = action ?? throw new ArgumentNullException(nameof(action));

            public void Dispose()
            {
                var action = _action;
                _action = null;
                action?.Invoke();
            }
        }
    }
}
