using System;
using System.Collections.Generic;

namespace Game.Core.DevTools
{
    /// <summary>Well-known fixture tags. A view may add its own.</summary>
    public static class FixtureTags
    {
        /// <summary>The minimal state every gate renders (required by ARCH101).</summary>
        public const string Smoke = "smoke";

        /// <summary>Extreme data: longest texts, empty lists, max counts. Need not be reachable in play.</summary>
        public const string Edge = "edge";

        /// <summary>Reached by running the real simulation with commands: always a state a player can see.</summary>
        public const string Scenario = "scenario";
    }

    /// <summary>
    /// One named state of a view, as typed C# (no JSON): applying it calls the view's Show* methods.
    /// The SAME fixture is used by the layout gate (L2/L3), the Editor preview window and screenshots,
    /// so "the state I checked by hand" and "the state the gate checks" can't drift apart.
    /// </summary>
    public sealed class ViewFixture<TView>
    {
        private readonly Action<TView> _apply;

        public string Name { get; }
        public IReadOnlyList<string> Tags { get; }

        public ViewFixture(string name, Action<TView> apply, params string[] tags)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { ' ', '@', '[', ']' }) >= 0)
                throw new ArgumentException("fixture name must be a non-empty id without spaces/@/[]: '" + name + "'", nameof(name));
            Name = name;
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Tags = tags ?? new string[0];
        }

        public bool Has(string tag)
        {
            foreach (var t in Tags)
                if (t == tag)
                    return true;
            return false;
        }

        public void Apply(TView view) => _apply(view);

        public override string ToString() => Name + (Tags.Count > 0 ? " [" + string.Join(",", Tags) + "]" : "");
    }
}
