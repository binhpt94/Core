using System.Linq;
using Game.Bootstrap.EditorTools;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Bootstrap.Tests.Validation
{
    /// <summary>EditMode tests for ARCH072 (required-by-default references).</summary>
    public sealed class ReferenceValidatorTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("Root");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void EmptyRequiredReferences_AreReported_OptionalIsNot()
        {
            _root.AddComponent<SampleView>();

            var errors = ReferenceValidator.ValidateHierarchy(_root, "test");

            Assert.That(errors.Any(e => e.StartsWith("ARCH072") && e.Contains("SampleView.title")), Is.True, string.Join("\n", errors));
            Assert.That(errors.Any(e => e.Contains("buttons.Array.data[0]")), Is.True, string.Join("\n", errors));
            Assert.That(errors.Any(e => e.Contains("subtitle")), Is.False, "[OptionalReference] must be skipped");
        }

        [Test]
        public void FilledReferences_AreClean()
        {
            var view = _root.AddComponent<SampleView>();
            var label = new GameObject("Label").AddComponent<Text>();
            label.transform.SetParent(_root.transform);
            var button = new GameObject("Button").AddComponent<Button>();
            button.transform.SetParent(_root.transform);

            view.Fill(label, button);

            Assert.That(ReferenceValidator.ValidateHierarchy(_root, "test"), Is.Empty);
        }

        [Test]
        public void NonGameComponents_AreIgnored()
        {
            _root.AddComponent<Image>(); // UnityEngine.UI: sprite may legitimately be empty

            Assert.That(ReferenceValidator.ValidateHierarchy(_root, "test"), Is.Empty);
        }
    }
}
