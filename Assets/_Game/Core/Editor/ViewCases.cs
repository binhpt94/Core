using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.DevTools;
using UnityEditor;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// How to test one view in isolation: create it, apply a named fixture. Discovered automatically
    /// (no list to maintain): every feature adds one class per view in Editor/Views/&lt;V&gt;Cases.cs (ARCH101).
    /// </summary>
    public interface IViewCases
    {
        /// <summary>"Feature/View", e.g. "MathQuiz/QuizView".</summary>
        string ViewId { get; }

        /// <summary>Repo-relative layout snapshot, owned by CODEOWNERS (Features/F/Tests/Layout/V.layout.txt).</summary>
        string BaselinePath { get; }

        IReadOnlyList<string> FixtureNames { get; }

        /// <summary>Fixtures in the L3 snapshot: all but scenario fixtures (those follow gameplay; L2 checks them).</summary>
        IReadOnlyList<string> SnapshotFixtureNames { get; }
        IReadOnlyList<DeviceProfile> Devices { get; }
        LayoutLimits Limits { get; }

        /// <summary>Instantiates the view (prefab the game ships, else its layout recipe) under parent. Returns the view object.</summary>
        object Create(Transform parent, out GameObject root);

        void Apply(object view, string fixture);
    }

    /// <summary>Typed base: a feature only says how to create its view and which fixtures exist.</summary>
    public abstract class ViewCases<TView> : IViewCases where TView : class
    {
        protected abstract string Feature { get; }
        protected abstract string View { get; }

        protected abstract IReadOnlyList<ViewFixture<TView>> Fixtures();

        protected abstract TView CreateView(Transform parent, out GameObject root);

        public string ViewId => Feature + "/" + View;
        public string BaselinePath => "Assets/_Game/Features/" + Feature + "/Tests/Layout/" + View + ".layout.txt";
        public IReadOnlyList<string> FixtureNames => Fixtures().Select(f => f.Name).ToList();
        public IReadOnlyList<string> SnapshotFixtureNames => Fixtures().Where(f => !f.Has(FixtureTags.Scenario)).Select(f => f.Name).ToList();
        public virtual IReadOnlyList<DeviceProfile> Devices => DeviceProfile.Standard();
        public virtual LayoutLimits Limits => new LayoutLimits();

        public object Create(Transform parent, out GameObject root) => CreateView(parent, out root);

        public void Apply(object view, string fixture)
        {
            var f = Fixtures().FirstOrDefault(x => x.Name == fixture)
                    ?? throw new ArgumentException(ViewId + " has no fixture '" + fixture + "'. Known: " + string.Join(", ", FixtureNames));
            f.Apply((TView)view);
        }
    }

    public static class ViewCasesRegistry
    {
        /// <summary>All view cases in the project, sorted by id (TypeCache: no reflection scan at runtime, Editor only).</summary>
        public static IReadOnlyList<IViewCases> All() =>
            TypeCache.GetTypesDerivedFrom<IViewCases>()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Select(t => (IViewCases)Activator.CreateInstance(t, true))
                .OrderBy(c => c.ViewId, StringComparer.Ordinal)
                .ToList();

        public static IViewCases Get(string viewId) =>
            All().FirstOrDefault(c => c.ViewId == viewId) ?? throw new ArgumentException("no view cases for '" + viewId + "'");

        /// <summary>NUnit category of a view's tests, used by the gate: -testCategory "View:MathQuiz.QuizView".</summary>
        public static string Category(string viewId) => "View:" + viewId.Replace('/', '.');
    }
}
