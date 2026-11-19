using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Bootstrap.Tests.Validation
{
    /// <summary>
    /// Test object: one required field, one optional, one required array.
    /// A ScriptableObject, not a MonoBehaviour: this assembly is Editor-only, and Unity refuses to add a component
    /// whose script lives in an editor assembly (AddComponent returns null), while CreateInstance works.
    /// </summary>
    public sealed class SampleView : ScriptableObject
    {
        [SerializeField] private Text title;
        [OptionalReference] [SerializeField] private Text subtitle = null;
        [SerializeField] private Button[] buttons = new Button[1];

        internal Text Subtitle => subtitle;

        internal void Fill(Text t, Button b)
        {
            title = t;
            buttons[0] = b;
        }
    }
}
