using System;
using System.Collections.Generic;

namespace Game.Core
{
    /// <summary>
    /// One allowed state-machine transition. A state machine declares ALL of them as a table:
    ///
    ///   internal static IReadOnlyList&lt;Transition&lt;MyState&gt;&gt; Transitions { get; } = new[]
    ///   {
    ///       new Transition&lt;MyState&gt;(MyState.A, "Start", MyState.B),   // one per line
    ///   };
    ///
    /// The table is the single source of truth twice over: the state machine refuses moves that are not
    /// in it (<see cref="TransitionTable.Ensure{TState}"/>), and `test-gate.sh graph` turns it into the
    /// state diagram in docs/architecture/overview.md. Code and diagram cannot drift apart.
    /// </summary>
    public readonly struct Transition<TState> where TState : struct, Enum
    {
        public TState From { get; }
        public string Trigger { get; }
        public TState To { get; }

        public Transition(TState from, string trigger, TState to)
        {
            From = from;
            Trigger = trigger;
            To = to;
        }

        public override string ToString() => From + " --" + Trigger + "--> " + To;
    }

    public static class TransitionTable
    {
        /// <summary>Throws (programmer error) if the move is not declared in the table.</summary>
        public static void Ensure<TState>(IReadOnlyList<Transition<TState>> table, TState from, string trigger, TState to)
            where TState : struct, Enum
        {
            var comparer = EqualityComparer<TState>.Default;
            for (int i = 0; i < table.Count; i++)
            {
                var t = table[i];
                if (comparer.Equals(t.From, from) && t.Trigger == trigger && comparer.Equals(t.To, to))
                    return;
            }
            throw new InvalidOperationException("Undeclared transition " + from + " --" + trigger + "--> " + to + ". Add it to the Transitions table.");
        }

        /// <summary>True if any transition leaves <paramref name="from"/> with <paramref name="trigger"/>.</summary>
        public static bool Allows<TState>(IReadOnlyList<Transition<TState>> table, TState from, string trigger)
            where TState : struct, Enum
        {
            var comparer = EqualityComparer<TState>.Default;
            for (int i = 0; i < table.Count; i++)
                if (comparer.Equals(table[i].From, from) && table[i].Trigger == trigger)
                    return true;
            return false;
        }
    }
}
